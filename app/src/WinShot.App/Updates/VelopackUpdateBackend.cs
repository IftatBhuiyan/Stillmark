using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using WinShot.Core.Update;

namespace WinShot.App.Updates;

/// <summary>
/// A feed result that has an authenticated, publication-dated catalog bound to the exact
/// Velopack full package. It is safe to hand to an entitlement and UI policy, but it does not
/// imply consent to download or restart.
/// </summary>
public sealed record VelopackUpdateTarget(
    UpdateInfo UpdateInfo,
    VelopackAsset Asset,
    VelopackReleaseCatalogEntry Catalog,
    string CatalogSha256);

/// <summary>
/// A downloaded package whose path and bytes still match the signed catalog. Apply rechecks both
/// the catalog provenance and package digest immediately before calling Velopack's restart API.
/// </summary>
public sealed record VelopackDownloadedUpdate(
    VelopackUpdateTarget Target,
    string PackagePath);

/// <summary>
/// Small application-side adapter over Velopack. It never applies on startup: callers explicitly
/// invoke <see cref="ApplyAndRestartAsync"/> after entitlement, active-work, and user-consent
/// policies have completed.
/// </summary>
public sealed class VelopackUpdateBackend : IDisposable
{
    public const string PackageId = "RelicScreenshot";
    public const string Channel = "stable";
    public const string RuntimeIdentifier = "win-x64";

    private readonly Uri _feedUri;
    private readonly string _publicKeyPem;
    private readonly string _expectedPublicKeySha256;
    private readonly HttpClient _httpClient;
    private readonly UpdateManager _manager;
    private bool _disposed;

    private sealed record FetchedCatalog(
        VelopackReleaseCatalogEntry Entry,
        string Sha256);

    private sealed class NoRedirectFileDownloader : HttpClientFileDownloader
    {
        private readonly Uri _origin;

        public NoRedirectFileDownloader(Uri origin) => _origin = origin;

        protected override HttpClientHandler CreateHttpClientHandler() => new()
        {
            AllowAutoRedirect = false,
        };

        public override Task DownloadFile(
            string url,
            string targetFile,
            Action<int>? progress,
            IDictionary<string, string>? headers,
            double timeout,
            CancellationToken cancelToken)
        {
            EnsureAllowed(url);
            progress ??= _ => { };
            return base.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken);
        }

        public override Task<byte[]> DownloadBytes(
            string url,
            IDictionary<string, string>? headers,
            double timeout)
        {
            EnsureAllowed(url);
            return base.DownloadBytes(url, headers, timeout);
        }

        public override Task<string> DownloadString(
            string url,
            IDictionary<string, string>? headers,
            double timeout)
        {
            EnsureAllowed(url);
            return base.DownloadString(url, headers, timeout);
        }

        private void EnsureAllowed(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? candidate)
                || candidate.Scheme != Uri.UriSchemeHttps
                || !candidate.Host.Equals(_origin.Host, StringComparison.OrdinalIgnoreCase)
                || candidate.Port != _origin.Port
                || !string.IsNullOrEmpty(candidate.UserInfo)
                || !string.IsNullOrEmpty(candidate.Query)
                || !string.IsNullOrEmpty(candidate.Fragment)
                || !_origin.IsBaseOf(candidate))
                throw new HttpRequestException("Velopack requested a URL outside the pinned update feed.");
        }
    }

    public VelopackUpdateBackend(
        Uri feedUri,
        string publicKeyPem,
        string expectedPublicKeySha256)
    {
        if (feedUri is null
            || feedUri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(feedUri.Host)
            || !string.IsNullOrEmpty(feedUri.UserInfo)
            || !string.IsNullOrEmpty(feedUri.Query)
            || !string.IsNullOrEmpty(feedUri.Fragment))
            throw new ArgumentException(
                "The update feed must be an HTTPS directory URI without query or fragment.",
                nameof(feedUri));
        if (!feedUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            feedUri = new Uri(feedUri.AbsoluteUri + "/", UriKind.Absolute);
        if (string.IsNullOrWhiteSpace(publicKeyPem)
            || string.IsNullOrWhiteSpace(expectedPublicKeySha256))
            throw new ArgumentException("A pinned catalog public key is required.");

        _feedUri = feedUri;
        _publicKeyPem = publicKeyPem;
        _expectedPublicKeySha256 = expectedPublicKeySha256;
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _manager = new UpdateManager(
            new StaticUpdateSource(
                _feedUri,
                new NoRedirectFileDownloader(_feedUri),
                0.5),
            new UpdateOptions
            {
                ExplicitChannel = Channel,
                AllowVersionDowngrade = false,
                MaximumDeltasBeforeFallback = 0,
            });
    }

    /// <summary>
    /// True only when Velopack recognizes the current process as an installed application that can
    /// check and download updates. Portable and development launches must keep the update action
    /// unavailable without attempting a feed request.
    /// </summary>
    public bool IsInstalled
    {
        get
        {
            ThrowIfDisposed();
            return _manager.IsInstalled;
        }
    }

    public async Task<VelopackUpdateTarget?> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_manager.IsInstalled)
            return null;

        UpdateInfo? update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (update is null || update.IsDowngrade)
            return null;

        VelopackAsset? asset = update.TargetFullRelease;
        if (!IsUsableFullAsset(asset))
            throw new InvalidDataException(
                "The update feed offered an invalid Stillmark package.");

        Version version = asset!.Version!.Version;
        FetchedCatalog? fetched = await FetchCatalogAsync(
            version,
            cancellationToken).ConfigureAwait(false);
        if (fetched is null
            || !VelopackReleaseCatalogVerifier.Matches(
                fetched.Entry,
                new VelopackReleaseExpectations(
                    "Stillmark",
                    PackageId,
                    Channel,
                    RuntimeIdentifier,
                    version,
                    asset.FileName))
            || fetched.Entry.PackageBytes != asset.Size
            || !fetched.Entry.PackageSha256.Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The update release catalog was missing, invalid, or did not match the package.");

        return new VelopackUpdateTarget(update, asset, fetched.Entry, fetched.Sha256);
    }

    public async Task<VelopackDownloadedUpdate?> DownloadAsync(
        VelopackUpdateTarget target,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(target);
        if (!IsUsableFullAsset(target.Asset)
            || !IsUsableFullAsset(target.UpdateInfo.TargetFullRelease)
            || !IsMatchingAsset(target.Asset, target.UpdateInfo.TargetFullRelease)
            || !IsMatchingAsset(target.Asset, target.Catalog))
            return null;
        if (!await IsCurrentCatalogAsync(target, cancellationToken).ConfigureAwait(false))
            return null;

        await _manager.DownloadUpdatesAsync(
            target.UpdateInfo,
            progress,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        string packagePath;
        try
        {
            packagePath = GetLocalPackagePath(target.Asset);
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or InvalidOperationException)
        {
            return null;
        }
        if (!VelopackReleaseCatalogVerifier.VerifyPackage(packagePath, target.Catalog))
            return null;
        return new VelopackDownloadedUpdate(target, packagePath);
    }

    /// <summary>
    /// Revalidates the immutable catalog and downloaded package, then asks Velopack to restart.
    /// If supplied, <paramref name="beforeRestart"/> is the final synchronous safety boundary;
    /// it must acquire the host's quiescence/exit gate and return true before Velopack is asked to
    /// restart. This method must be called only from an explicit user-approved restart action.
    /// </summary>
    public async Task<bool> ApplyAndRestartAsync(
        VelopackDownloadedUpdate downloaded,
        string[] restartArguments,
        CancellationToken cancellationToken = default,
        Func<bool>? beforeRestart = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(downloaded);
        ArgumentNullException.ThrowIfNull(restartArguments);
        if (!IsUsableFullAsset(downloaded.Target.Asset)
            || !IsMatchingAsset(downloaded.Target.Asset, downloaded.Target.Catalog))
            return false;
        if (!await IsCurrentCatalogAsync(
                downloaded.Target,
                cancellationToken).ConfigureAwait(false)
            || !VelopackReleaseCatalogVerifier.VerifyPackage(
                downloaded.PackagePath,
                downloaded.Target.Catalog))
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        if (beforeRestart is not null && !beforeRestart())
            return false;
        _manager.ApplyUpdatesAndRestart(downloaded.Target.Asset, restartArguments);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _httpClient.Dispose();
    }

    private async Task<FetchedCatalog?> FetchCatalogAsync(
        Version version,
        CancellationToken cancellationToken)
    {
        string catalogName = VelopackReleaseCatalogVerifier.GetCatalogFileName(PackageId, version);
        Uri catalogUri = new(_feedUri, catalogName);
        Uri signatureUri = new(
            _feedUri,
            Path.GetFileNameWithoutExtension(catalogName) + ".sig");
        try
        {
            using HttpResponseMessage catalogResponse = await _httpClient.GetAsync(
                catalogUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            using HttpResponseMessage signatureResponse = await _httpClient.GetAsync(
                signatureUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (catalogResponse.StatusCode != HttpStatusCode.OK
                || signatureResponse.StatusCode != HttpStatusCode.OK)
                return null;
            byte[] catalogBytes = await ReadBoundedAsync(
                catalogResponse.Content,
                VelopackReleaseCatalogVerifier.MaximumCatalogBytes,
                cancellationToken).ConfigureAwait(false);
            byte[] signatureBytes = await ReadBoundedAsync(
                signatureResponse.Content,
                VelopackReleaseCatalogVerifier.MaximumSignatureBytes,
                cancellationToken).ConfigureAwait(false);
            if (!VelopackReleaseCatalogVerifier.TryVerify(
                    catalogBytes,
                    signatureBytes,
                    _publicKeyPem,
                    _expectedPublicKeySha256,
                    out VelopackReleaseCatalogEntry? entry)
                || entry is null)
                return null;
            return new FetchedCatalog(
                entry,
                Convert.ToHexString(SHA256.HashData(catalogBytes)));
        }
        catch (Exception ex) when (ex is HttpRequestException
            or IOException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<bool> IsCurrentCatalogAsync(
        VelopackUpdateTarget target,
        CancellationToken cancellationToken)
    {
        FetchedCatalog? fetched = await FetchCatalogAsync(
            target.Asset.Version.Version,
            cancellationToken).ConfigureAwait(false);
        return fetched is not null
            && fetched.Sha256.Equals(target.CatalogSha256, StringComparison.Ordinal)
            && fetched.Entry == target.Catalog;
    }

    private static string GetLocalPackagePath(VelopackAsset asset)
    {
        string currentDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetFileName(currentDirectory).Equals(
                "current",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Velopack package verification requires the installed current layout.");
        string packageDirectory = Path.Combine(
            Directory.GetParent(currentDirectory)!.FullName,
            "packages");
        return Path.Combine(packageDirectory, asset.FileName);
    }

    private static bool IsUsableFullAsset(VelopackAsset? asset) =>
        asset is not null
        && asset.Type == VelopackAssetType.Full
        && string.Equals(asset.PackageId, PackageId, StringComparison.Ordinal)
        && asset.Version?.Version is not null
        && !asset.Version.IsPrerelease
        && VelopackReleaseCatalogVerifier.IsSafePackageFileName(asset.FileName)
        && !string.IsNullOrWhiteSpace(asset.SHA256)
        && asset.Size > 0;

    private static bool IsMatchingAsset(
        VelopackAsset? asset,
        VelopackAsset? expected)
    {
        if (!IsUsableFullAsset(asset) || !IsUsableFullAsset(expected))
            return false;
        return expected!.PackageId.Equals(asset!.PackageId, StringComparison.Ordinal)
            && expected.Version!.Version == asset.Version!.Version
            && expected.FileName.Equals(asset.FileName, StringComparison.Ordinal)
            && expected.Size == asset.Size
            && expected.SHA256.Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMatchingAsset(
        VelopackAsset? asset,
        VelopackReleaseCatalogEntry? catalog)
    {
        if (!IsUsableFullAsset(asset) || catalog is null)
            return false;
        return asset!.Version!.Version == catalog.Version
            && asset.FileName.Equals(catalog.PackageFileName, StringComparison.Ordinal)
            && asset.Size == catalog.PackageBytes
            && asset.SHA256.Equals(catalog.PackageSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength
            && contentLength > maximumBytes)
            throw new InvalidDataException("The signed catalog response exceeded its size budget.");

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using MemoryStream buffer = new(Math.Min(
            maximumBytes,
            checked((int)(content.Headers.ContentLength ?? maximumBytes))));
        byte[] chunk = new byte[Math.Min(81920, maximumBytes)];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("The signed catalog response exceeded its size budget.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VelopackUpdateBackend));
    }
}
