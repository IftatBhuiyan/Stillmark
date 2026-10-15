using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinShot.Licensing.Accounts;

public sealed record DesktopAccountClientOptions
{
    public DesktopAccountClientOptions(Uri supabaseUrl, string publishableKey)
    {
        ArgumentNullException.ThrowIfNull(supabaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishableKey);
        if (!supabaseUrl.IsAbsoluteUri
            || supabaseUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(supabaseUrl.UserInfo)
            || !string.IsNullOrEmpty(supabaseUrl.Query)
            || !string.IsNullOrEmpty(supabaseUrl.Fragment))
            throw new ArgumentException("The account service URL must be an HTTPS origin.", nameof(supabaseUrl));

        SupabaseUrl = new Uri(supabaseUrl.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        PublishableKey = publishableKey.Trim();
    }

    public Uri SupabaseUrl { get; }
    public string PublishableKey { get; }
    public string? SessionFilePath { get; init; }

    public override string ToString() => "[account client options]";
}

public sealed record AccountSessionInfo(string? Email, DateTimeOffset ExpiresAtUtc);

public sealed record AccountLicense(
    string Id,
    DateTimeOffset PurchasedAtUtc,
    DateTimeOffset UpdatesUntilUtc,
    int SeatLimit,
    string Status,
    IReadOnlyList<AccountDevice> Devices);

public sealed record AccountDevice(string Id, string Name, DateTimeOffset ActivatedAtUtc);

public sealed record AccountSnapshot(string Email, IReadOnlyList<AccountLicense> Licenses);

public sealed record AccountActivation(string LicenseKey, Guid? ActivationId);

public sealed record AccountRelease(
    string Version,
    DateTimeOffset ReleasedAtUtc,
    Uri DownloadUrl,
    string Sha256);

public enum StoredActivationRefreshOutcome
{
    Refreshed,
    Current,
    NoActivation,
    SignInRequired,
    Revoked,
    Unavailable,
    InvalidResponse,
}

public sealed record StoredActivationRefreshResult(
    StoredActivationRefreshOutcome Outcome,
    LicenseStatus? Status = null)
{
    public bool Succeeded => Outcome is StoredActivationRefreshOutcome.Refreshed
        or StoredActivationRefreshOutcome.Current;
}

public sealed class AccountAuthenticationException : Exception
{
    internal AccountAuthenticationException(string message = "Sign in is required.")
        : base(message) { }
}

public sealed class AccountApiException : Exception
{
    internal AccountApiException(HttpStatusCode? statusCode, string message)
        : base(message) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }
    public bool IsExplicitRevocation => StatusCode is HttpStatusCode.Forbidden
        or HttpStatusCode.NotFound
        or (HttpStatusCode)410;
}

public sealed class DesktopAccountClient : IDisposable
{
    private const int MaximumJsonBytes = 128 * 1024;
    private const int MaximumTokenCharacters = 32 * 1024;
    private const int MaximumEmailCharacters = 320;
    private const int MaximumDeviceNameCharacters = 128;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AccessTokenRefreshSkew = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefreshRotationMinimumInterval = TimeSpan.FromMinutes(1);

    private readonly DesktopAccountClientOptions _options;
    private readonly HttpClient _http;
    private readonly IAccountSessionStore _sessions;
    private readonly Func<string?> _deviceId;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Uri _authBase;
    private readonly Uri _edgeBase;
    private bool _disposed;

    public DesktopAccountClient(
        DesktopAccountClientOptions options,
        HttpMessageHandler? handler = null,
        IAccountSessionStore? sessionStore = null)
        : this(options, handler, sessionStore, () => LicenseDeviceIdentity.Current, () => DateTimeOffset.UtcNow)
    {
    }

    internal DesktopAccountClient(
        DesktopAccountClientOptions options,
        HttpMessageHandler? handler,
        IAccountSessionStore? sessionStore,
        Func<string?> deviceId,
        Func<DateTimeOffset> now)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(now);
        _options = options;
        _deviceId = deviceId;
        _now = now;
        _sessions = sessionStore ?? new DesktopAccountSessionStore(options.SessionFilePath);
        _authBase = new Uri(options.SupabaseUrl, "auth/v1/");
        _edgeBase = new Uri(options.SupabaseUrl, "functions/v1/relic-api/");
        _http = new HttpClient(handler ?? CreateDefaultHandler(), disposeHandler: true)
        {
            BaseAddress = options.SupabaseUrl,
            Timeout = RequestTimeout,
        };
    }

    public async Task RequestEmailCodeAsync(string email, CancellationToken ct = default)
    {
        string normalizedEmail = NormalizeEmail(email);
        await SendJsonAsync<object?>(
            HttpMethod.Post,
            new Uri(_authBase, "otp"),
            new { email = normalizedEmail, create_user = true },
            authenticated: false,
            expected: HttpStatusCode.OK,
            ct);
    }

    public async Task<AccountSessionInfo> VerifyEmailCodeAsync(
        string email,
        string token,
        CancellationToken ct = default)
    {
        string normalizedEmail = NormalizeEmail(email);
        string normalizedToken = NormalizeToken(token, nameof(token));
        AuthResponse response = await SendJsonAsync<AuthResponse>(
            HttpMethod.Post,
            new Uri(_authBase, "verify"),
            new { email = normalizedEmail, token = normalizedToken, type = "email" },
            authenticated: false,
            expected: HttpStatusCode.OK,
            ct) ?? throw InvalidResponse();
        AccountSession session = CreateSession(response, normalizedEmail);
        _sessions.Save(session);
        return session.ToInfo();
    }

    public async Task<AccountSessionInfo> RefreshSessionAsync(CancellationToken ct = default)
    {
        AccountSession session = await RefreshStoredSessionAsync(force: true, ct);
        return session.ToInfo();
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        AccountSession? session = _sessions.Load();
        if (session is null)
            return;

        try
        {
            await SendJsonAsync<object?>(
                HttpMethod.Post,
                new Uri(_authBase, "logout?scope=local"),
                body: null,
                authenticated: true,
                expected: HttpStatusCode.NoContent,
                ct);
            _sessions.Clear();
        }
        catch (AccountAuthenticationException)
        {
            _sessions.Clear();
            throw;
        }
    }

    public async Task<AccountSnapshot> GetAccountAsync(CancellationToken ct = default)
    {
        AccountSnapshotDto response = await SendJsonAsync<AccountSnapshotDto>(
            HttpMethod.Get,
            new Uri(_edgeBase, "account"),
            body: null,
            authenticated: true,
            expected: HttpStatusCode.OK,
            ct) ?? throw InvalidResponse();
        return response.ToModel();
    }

    public async Task<AccountActivation> ActivateAsync(
        string licenseId,
        string deviceName,
        CancellationToken ct = default)
    {
        string deviceId = RequireDeviceId();
        ValidateIdentifier(licenseId, nameof(licenseId), 256);
        ValidateDeviceName(deviceName);
        ActivationResponseDto response = await SendJsonAsync<ActivationResponseDto>(
            HttpMethod.Post,
            new Uri(_edgeBase, "activations"),
            new { licenseId, deviceId, deviceName = deviceName.Trim() },
            authenticated: true,
            expected: HttpStatusCode.OK,
            ct) ?? throw InvalidResponse();
        return response.ToModel(requireActivationId: true);
    }

    public async Task<AccountActivation> RefreshActivationAsync(
        Guid activationId,
        CancellationToken ct = default)
    {
        string deviceId = RequireDeviceId();
        ActivationResponseDto response = await SendJsonAsync<ActivationResponseDto>(
            HttpMethod.Post,
            new Uri(_edgeBase, $"activations/{activationId:D}/refresh"),
            new { deviceId },
            authenticated: true,
            expected: HttpStatusCode.OK,
            ct) ?? throw InvalidResponse();
        if (response.ActivationId is { } returned && returned != activationId)
            throw InvalidResponse();
        return response.ToModel(requireActivationId: false);
    }

    public async Task DeactivateAsync(Guid activationId, CancellationToken ct = default)
    {
        await SendJsonAsync<object?>(
            HttpMethod.Delete,
            new Uri(_edgeBase, $"activations/{activationId:D}"),
            body: null,
            authenticated: true,
            expected: HttpStatusCode.NoContent,
            ct);
    }

    public async Task<IReadOnlyList<AccountRelease>> GetReleasesAsync(CancellationToken ct = default)
    {
        ReleasesResponseDto response = await SendJsonAsync<ReleasesResponseDto>(
            HttpMethod.Get,
            new Uri(_edgeBase, "releases"),
            body: null,
            authenticated: false,
            expected: HttpStatusCode.OK,
            ct) ?? throw InvalidResponse();
        if (response.Releases is null || response.Releases.Length > 256)
            throw InvalidResponse();
        return response.Releases.Select(item => item.ToModel(_options.SupabaseUrl)).ToArray();
    }

    public async Task<StoredActivationRefreshResult> RefreshStoredActivationAsync(
        LicenseService licenseService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(licenseService);
        LicenseStatus status = licenseService.Status;
        LicensePayload? payload = status.License;
        string? deviceId = _deviceId();
        if (payload is null || payload.Version != 3)
            return new(StoredActivationRefreshOutcome.NoActivation, status);
        if (!LicenseEntitlement.IsValid(payload) || !LicenseEntitlement.MatchesDevice(payload, deviceId))
            return new(StoredActivationRefreshOutcome.NoActivation, status);

        DateTimeOffset now = _now();
        if (now <= DateTimeOffset.FromUnixTimeSeconds(payload.LeaseIssuedAt).AddDays(1))
            return new(StoredActivationRefreshOutcome.Current, status);

        if (!Guid.TryParseExact(payload.ActivationId, "D", out Guid activationId))
            return new(StoredActivationRefreshOutcome.InvalidResponse, status);

        try
        {
            await RefreshStoredSessionIfNeededAsync(ct);
        }
        catch (AccountAuthenticationException)
        {
            return new(StoredActivationRefreshOutcome.SignInRequired, status);
        }
        catch (AccountApiException ex) when (ex.IsExplicitRevocation)
        {
            // A rejected auth refresh is a sign-in problem, not proof that the local license
            // was revoked. Preserve the offline lease until the user signs in again.
            return new(StoredActivationRefreshOutcome.SignInRequired, status);
        }
        catch (AccountApiException)
        {
            return new(StoredActivationRefreshOutcome.Unavailable, status);
        }
        catch (HttpRequestException)
        {
            return new(StoredActivationRefreshOutcome.Unavailable, status);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(StoredActivationRefreshOutcome.Unavailable, status);
        }

        try
        {
            AccountActivation refreshed = await RefreshActivationAsync(activationId, ct);
            if (!LicenseKey.TryVerify(refreshed.LicenseKey, out LicensePayload? refreshedPayload)
                || refreshedPayload is null
                || refreshedPayload.Version != 3
                || !LicenseEntitlement.IsValid(refreshedPayload)
                || !string.Equals(refreshedPayload.Id, payload.Id, StringComparison.Ordinal)
                || !string.Equals(refreshedPayload.DeviceId, deviceId, StringComparison.Ordinal)
                || !string.Equals(refreshedPayload.ActivationId, payload.ActivationId, StringComparison.Ordinal)
                || refreshedPayload.LeaseIssuedAt <= payload.LeaseIssuedAt)
                return new(StoredActivationRefreshOutcome.InvalidResponse, status);

            ActivationResult activation = licenseService.Activate(refreshed.LicenseKey);
            return activation.Succeeded
                ? new(StoredActivationRefreshOutcome.Refreshed, activation.Status)
                : new(StoredActivationRefreshOutcome.InvalidResponse, status);
        }
        catch (AccountAuthenticationException)
        {
            return new(StoredActivationRefreshOutcome.SignInRequired, status);
        }
        catch (AccountApiException ex) when (ex.IsExplicitRevocation)
        {
            licenseService.Deactivate();
            return new(StoredActivationRefreshOutcome.Revoked, licenseService.Status);
        }
        catch (AccountApiException)
        {
            return new(StoredActivationRefreshOutcome.Unavailable, status);
        }
        catch (HttpRequestException)
        {
            return new(StoredActivationRefreshOutcome.Unavailable, status);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(StoredActivationRefreshOutcome.Unavailable, status);
        }
    }

    private async Task RefreshStoredSessionIfNeededAsync(CancellationToken ct)
    {
        AccountSession? stored = _sessions.Load();
        if (stored is null)
            throw new AccountAuthenticationException();
        DateTimeOffset now = _now();
        if (stored.LastRefreshedAtUtc > now - TimeSpan.FromDays(1)
            && stored.ExpiresAtUtc > now + AccessTokenRefreshSkew)
            return;
        _ = await RefreshStoredSessionAsync(force: false, ct);
    }

    private async Task<AccountSession> RequireAccessSessionAsync(CancellationToken ct)
    {
        AccountSession? session = _sessions.Load();
        if (session is null)
            throw new AccountAuthenticationException();
        if (session.ExpiresAtUtc > _now() + AccessTokenRefreshSkew)
            return session;
        return await RefreshStoredSessionAsync(force: false, ct);
    }

    private async Task<AccountSession> RefreshStoredSessionAsync(bool force, CancellationToken ct)
    {
        AccountSession? existing = _sessions.Load();
        if (existing is null)
            throw new AccountAuthenticationException();

        await _refreshGate.WaitAsync(ct);
        try
        {
            existing = _sessions.Load();
            if (existing is null)
                throw new AccountAuthenticationException();
            DateTimeOffset now = _now();
            if ((!force && existing.ExpiresAtUtc > now + AccessTokenRefreshSkew)
                || (force && existing.LastRefreshedAtUtc > now - RefreshRotationMinimumInterval
                    && existing.ExpiresAtUtc > now + AccessTokenRefreshSkew))
                return existing;

            AuthResponse response;
            try
            {
                response = await SendJsonAsync<AuthResponse>(
                    HttpMethod.Post,
                    new Uri(_authBase, "token?grant_type=refresh_token"),
                    new { refresh_token = existing.RefreshToken },
                    authenticated: false,
                    expected: HttpStatusCode.OK,
                    ct) ?? throw InvalidResponse();
            }
            catch (AccountApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.Unauthorized)
            {
                _sessions.Clear();
                throw new AccountAuthenticationException();
            }

            AccountSession refreshed = CreateSession(response, existing.Email);
            _sessions.Save(refreshed);
            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<T?> SendJsonAsync<T>(
        HttpMethod method,
        Uri uri,
        object? body,
        bool authenticated,
        HttpStatusCode expected,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        ValidateUri(uri);
        AccountSession? session = authenticated ? await RequireAccessSessionAsync(ct) : null;
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("apikey", _options.PublishableKey);
        if (session is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        if (body is not null)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
            if (json.Length > MaximumJsonBytes)
                throw InvalidResponse();
            request.Content = new ByteArrayContent(json);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using HttpResponseMessage response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated)
        {
            _sessions.Clear();
            throw new AccountAuthenticationException();
        }
        if (response.StatusCode != expected
            && !(expected == HttpStatusCode.NoContent && response.StatusCode == HttpStatusCode.OK))
            throw new AccountApiException(response.StatusCode, "The account service rejected the request.");
        if (typeof(T) == typeof(object))
            return default;
        return await ReadJsonAsync<T>(response, ct);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new BoundedReadStream(stream, MaximumJsonBytes);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(limited, JsonOptions, ct);
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
        catch (IOException)
        {
            throw InvalidResponse();
        }
    }

    private static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = true,
    };

    private void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(_options.SupabaseUrl.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _options.SupabaseUrl.Port
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !_options.SupabaseUrl.IsBaseOf(uri)
            || !IsAllowedQuery(uri))
            throw new AccountApiException(null, "The account service endpoint is not trusted.");
    }

    private bool IsAllowedQuery(Uri uri) =>
        string.IsNullOrEmpty(uri.Query)
        || (uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
            && uri.Query.Equals("?grant_type=refresh_token", StringComparison.Ordinal))
        || (uri.AbsolutePath.EndsWith("/logout", StringComparison.Ordinal)
            && uri.Query.Equals("?scope=local", StringComparison.Ordinal));

    private string RequireDeviceId() => LicenseEntitlement.IsValidDeviceId(_deviceId())
        ? _deviceId()!
        : throw new AccountApiException(null, "This computer has no usable device identity.");

    private static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        string normalized = email.Trim();
        if (normalized.Length > MaximumEmailCharacters
            || normalized.Any(c => c is '\r' or '\n' or <= '\u001F'))
            throw new ArgumentException("The email address is invalid.", nameof(email));
        return normalized;
    }

    private static string NormalizeToken(string token, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        string normalized = token.Trim();
        if (normalized.Length > MaximumTokenCharacters
            || normalized.Any(c => c is '\r' or '\n' or <= '\u001F'))
            throw new ArgumentException("The verification code is invalid.", parameterName);
        return normalized;
    }

    private static void ValidateIdentifier(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > maxLength || value.Any(c => c is '\r' or '\n' or <= '\u001F'))
            throw new ArgumentException("The identifier is invalid.", parameterName);
    }

    private static void ValidateDeviceName(string value) =>
        ValidateIdentifier(value, nameof(value), MaximumDeviceNameCharacters);

    private static AccountApiException InvalidResponse() =>
        new(null, "The account service returned an invalid response.");

    private AccountSession CreateSession(AuthResponse response, string? fallbackEmail)
    {
        if (string.IsNullOrWhiteSpace(response.AccessToken)
            || string.IsNullOrWhiteSpace(response.RefreshToken)
            || response.ExpiresIn <= 0
            || response.ExpiresIn > 60 * 60 * 24 * 90)
            throw InvalidResponse();
        string access = NormalizeToken(response.AccessToken, nameof(response.AccessToken));
        string refresh = NormalizeToken(response.RefreshToken, nameof(response.RefreshToken));
        DateTimeOffset now = _now();
        DateTimeOffset expires = now.AddSeconds(response.ExpiresIn);
        string? email = response.User?.Email ?? fallbackEmail;
        return new AccountSession(access, refresh, expires, email, now);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DesktopAccountClient));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
        _refreshGate.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        Converters = { new FlexibleDateTimeOffsetConverter() },
    };

    private sealed record AuthResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn,
        [property: JsonPropertyName("user")] AuthUser? User);

    private sealed record AuthUser([property: JsonPropertyName("email")] string? Email);

    private sealed record AccountSnapshotDto(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("licenses")] AccountLicenseDto[]? Licenses)
    {
        public AccountSnapshot ToModel()
        {
            if (string.IsNullOrWhiteSpace(Email) || Licenses is null || Licenses.Length > 128)
                throw InvalidResponse();
            if (Licenses.Any(static item => item is null))
                throw InvalidResponse();
            return new AccountSnapshot(Email, Licenses.Select(static item => item!.ToModel()).ToArray());
        }
    }

    private sealed record AccountLicenseDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("purchasedAt")] DateTimeOffset? PurchasedAtUtc,
        [property: JsonPropertyName("updatesUntil")] DateTimeOffset? UpdatesUntilUtc,
        [property: JsonPropertyName("seatLimit")] int SeatLimit,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("devices")] AccountDeviceDto[]? Devices)
    {
        public AccountLicense ToModel()
        {
            if (string.IsNullOrWhiteSpace(Id)
                || PurchasedAtUtc is not { } purchased
                || UpdatesUntilUtc is not { } updates
                || SeatLimit <= 0
                || SeatLimit > 1000
                || string.IsNullOrWhiteSpace(Status)
                || Devices is null
                || Devices.Length > 1000)
                throw InvalidResponse();
            if (Devices.Any(static item => item is null))
                throw InvalidResponse();
            return new AccountLicense(
                Id,
                purchased,
                updates,
                SeatLimit,
                Status,
                Devices.Select(static item => item!.ToModel()).ToArray());
        }
    }

    private sealed record AccountDeviceDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("activatedAt")] DateTimeOffset? ActivatedAtUtc)
    {
        public AccountDevice ToModel()
        {
            if (string.IsNullOrWhiteSpace(Id)
                || string.IsNullOrWhiteSpace(Name)
                || ActivatedAtUtc is not { } activated)
                throw InvalidResponse();
            return new AccountDevice(Id, Name, activated);
        }
    }

    private sealed record ActivationResponseDto(
        [property: JsonPropertyName("licenseKey")] string? LicenseKey,
        [property: JsonPropertyName("activationId")] Guid? ActivationId)
    {
        public AccountActivation ToModel(bool requireActivationId)
        {
            if (string.IsNullOrWhiteSpace(LicenseKey)
                || LicenseKey.Length > 8 * 1024
                || requireActivationId && ActivationId is null)
                throw InvalidResponse();
            return new AccountActivation(LicenseKey, ActivationId);
        }
    }

    private sealed record ReleaseResponseDto(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("releasedAt")] DateTimeOffset? ReleasedAtUtc,
        [property: JsonPropertyName("downloadUrl")] Uri? DownloadUrl,
        [property: JsonPropertyName("sha256")] string? Sha256)
    {
        public AccountRelease ToModel(Uri trustedOrigin)
        {
            if (string.IsNullOrWhiteSpace(Version)
                || Version.Length > 64
                || ReleasedAtUtc is not { } released
                || DownloadUrl is null
                || DownloadUrl.Scheme != Uri.UriSchemeHttps
                || !DownloadUrl.Host.Equals(trustedOrigin.Host, StringComparison.OrdinalIgnoreCase)
                || DownloadUrl.Port != trustedOrigin.Port
                || !string.IsNullOrEmpty(DownloadUrl.UserInfo)
                || !string.IsNullOrEmpty(DownloadUrl.Query)
                || !string.IsNullOrEmpty(DownloadUrl.Fragment)
                || string.IsNullOrWhiteSpace(Sha256)
                || Sha256.Length != 64
                || Sha256.Any(c => !Uri.IsHexDigit(c)))
                throw InvalidResponse();
            return new AccountRelease(Version, released, DownloadUrl, Sha256.ToLowerInvariant());
        }
    }

    private sealed record ReleasesResponseDto(
        [property: JsonPropertyName("releases")] ReleaseResponseDto[]? Releases);

    private sealed class FlexibleDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
    {
        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && DateTimeOffset.TryParse(reader.GetString(), out DateTimeOffset parsed))
                return parsed.ToUniversalTime();
            if (reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt64(out long seconds))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { }
            }
            if (reader.TokenType == JsonTokenType.Null)
                return null;
            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    private sealed class BoundedReadStream(Stream inner, int maximumBytes) : Stream
    {
        private int _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.CanSeek ? Math.Min(inner.Length, maximumBytes) : _read;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= maximumBytes)
                throw new IOException("The account response is too large.");
            int allowed = Math.Min(buffer.Length, maximumBytes - _read);
            int read = await inner.ReadAsync(buffer[..allowed], cancellationToken);
            _read += read;
            if (_read > maximumBytes)
                throw new IOException("The account response is too large.");
            return read;
        }
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
