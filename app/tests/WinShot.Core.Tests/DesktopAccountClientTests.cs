using System.Net;
using System.Net.Http;
using System.Text;
using WinShot.Licensing;
using WinShot.Licensing.Accounts;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class DesktopAccountClientTests
{
    private static readonly Uri ServiceUri = new("https://kwbjlzozcaeqzdzcexzq.supabase.co/");
    private static readonly string DeviceId = new('a', 64);

    [Fact]
    public async Task Parallel_refreshes_rotate_the_refresh_token_once()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var store = new MemorySessionStore(new AccountSession(
            "old-access",
            "old-refresh",
            now.AddSeconds(-1),
            "buyer@example.test",
            now.AddDays(-2)));
        int refreshCalls = 0;
        using var client = CreateClient(
            store,
            now,
            async request =>
            {
                Assert.Equal("?grant_type=refresh_token", request.RequestUri!.Query);
                Interlocked.Increment(ref refreshCalls);
                await Task.Delay(20);
                return Json(HttpStatusCode.OK, """
                    {"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"user":{"email":"buyer@example.test"}}
                    """);
            });

        AccountSessionInfo[] results = await Task.WhenAll(
            client.RefreshSessionAsync(),
            client.RefreshSessionAsync());

        Assert.Equal(1, refreshCalls);
        Assert.All(results, result => Assert.Equal(now.AddSeconds(3600), result.ExpiresAtUtc));
        Assert.Equal("new-refresh", store.Load()!.RefreshToken);
    }

    [Fact]
    public async Task Releases_accept_the_edge_response_envelope()
    {
        DateTimeOffset now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        using var client = CreateClient(
            new MemorySessionStore(null),
            now,
            _ => Task.FromResult(Json(HttpStatusCode.OK, $$"""
                {"releases":[{"version":"1.2.3","releasedAt":"{{now:O}}","downloadUrl":"{{ServiceUri}}downloads/relic.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
                """)));

        IReadOnlyList<AccountRelease> releases = await client.GetReleasesAsync();

        AccountRelease release = Assert.Single(releases);
        Assert.Equal("1.2.3", release.Version);
        Assert.Equal(now, release.ReleasedAtUtc);
        Assert.Equal("https://kwbjlzozcaeqzdzcexzq.supabase.co/downloads/relic.exe", release.DownloadUrl.AbsoluteUri);
        Assert.Equal(new string('a', 64), release.Sha256);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData((HttpStatusCode)410)]
    public async Task Explicit_server_revocation_clears_only_the_local_activation(HttpStatusCode statusCode)
    {
        DateTimeOffset now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Guid activationId = Guid.NewGuid();
        LicensePayload payload = Payload(now, activationId);
        string oldKey = "old-key";
        string root = Path.Combine(Path.GetTempPath(), "relic-account-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new LicenseStorage(root);
            storage.Save(oldKey);
            var service = new LicenseService(
                new TrialTracker(root, () => now, useRegistry: false),
                storage,
                1,
                key => key == oldKey ? payload : null,
                LicenseEnforcementOptions.OfflineSigned,
                currentReleaseDateUtc: now.AddDays(-2),
                deviceId: () => DeviceId,
                now: () => now);
            var store = new MemorySessionStore(new AccountSession(
                "access", "refresh", now.AddHours(1), "buyer@example.test", now));
            using var client = CreateClient(store, now, _ => Task.FromResult(new HttpResponseMessage(statusCode)));

            StoredActivationRefreshResult result = await client.RefreshStoredActivationAsync(service);

            Assert.Equal(StoredActivationRefreshOutcome.Revoked, result.Outcome);
            Assert.Null(storage.Load());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Invalid_refresh_license_keeps_the_previous_activation()
    {
        DateTimeOffset now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Guid activationId = Guid.NewGuid();
        LicensePayload payload = Payload(now, activationId);
        string oldKey = "old-key";
        string root = Path.Combine(Path.GetTempPath(), "relic-account-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new LicenseStorage(root);
            storage.Save(oldKey);
            var service = new LicenseService(
                new TrialTracker(root, () => now, useRegistry: false),
                storage,
                1,
                key => key == oldKey ? payload : null,
                LicenseEnforcementOptions.OfflineSigned,
                currentReleaseDateUtc: now.AddDays(-2),
                deviceId: () => DeviceId,
                now: () => now);
            var store = new MemorySessionStore(new AccountSession(
                "access", "refresh", now.AddHours(1), "buyer@example.test", now));
            using var client = CreateClient(
                store,
                now,
                _ => Task.FromResult(Json(HttpStatusCode.OK, $$"""
                    {"licenseKey":"not-a-signed-key","activationId":"{{activationId:D}}"}
                    """)));

            StoredActivationRefreshResult result = await client.RefreshStoredActivationAsync(service);

            Assert.Equal(StoredActivationRefreshOutcome.InvalidResponse, result.Outcome);
            Assert.Equal(oldKey, storage.Load());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Unauthorized_refresh_requests_sign_in_without_clearing_the_license()
    {
        DateTimeOffset now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Guid activationId = Guid.NewGuid();
        LicensePayload payload = Payload(now, activationId);
        string oldKey = "old-key";
        string root = Path.Combine(Path.GetTempPath(), "relic-account-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new LicenseStorage(root);
            storage.Save(oldKey);
            var service = new LicenseService(
                new TrialTracker(root, () => now, useRegistry: false),
                storage,
                1,
                key => key == oldKey ? payload : null,
                LicenseEnforcementOptions.OfflineSigned,
                currentReleaseDateUtc: now.AddDays(-2),
                deviceId: () => DeviceId,
                now: () => now);
            var store = new MemorySessionStore(new AccountSession(
                "access", "refresh", now.AddHours(1), "buyer@example.test", now));
            using var client = CreateClient(
                store,
                now,
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

            StoredActivationRefreshResult result = await client.RefreshStoredActivationAsync(service);

            Assert.Equal(StoredActivationRefreshOutcome.SignInRequired, result.Outcome);
            Assert.Equal(oldKey, storage.Load());
            Assert.Null(store.Load());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static DesktopAccountClient CreateClient(
        MemorySessionStore store,
        DateTimeOffset now,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(
            new DesktopAccountClientOptions(ServiceUri, "sb_publishable_test"),
            new StubHandler(send),
            store,
            () => DeviceId,
            () => now);

    private static LicensePayload Payload(DateTimeOffset now, Guid activationId) => new()
    {
        Version = 3,
        Id = "license-1",
        Product = "winshot",
        Edition = "pro",
        Major = 1,
        IssuedAt = now.AddYears(-1).ToUnixTimeSeconds(),
        UpdatesUntil = now.AddYears(1).ToUnixTimeSeconds(),
        DeviceId = DeviceId,
        ActivationId = activationId.ToString("D"),
        LeaseIssuedAt = now.AddDays(-2).ToUnixTimeSeconds(),
        ValidUntil = now.AddDays(28).ToUnixTimeSeconds(),
    };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    private sealed class MemorySessionStore(AccountSession? initial) : IAccountSessionStore
    {
        private AccountSession? _session = initial;
        public AccountSession? Load() => Volatile.Read(ref _session);
        public void Save(AccountSession session) => Volatile.Write(ref _session, session);
        public void Clear() => Volatile.Write(ref _session, null);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }
}
