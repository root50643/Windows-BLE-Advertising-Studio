using System.Net;
using System.Net.Http;
using BLEStudio.Infrastructure;

namespace BLEStudio.IntegrationTests;

public class TrackingClientTests
{
    [Fact] public void PlainHttpAndEmbeddedCredentialsRejected()
    {
        Assert.Throws<FormatException>(() => new TrackingClient("http://example.com", "test"));
        Assert.Throws<FormatException>(() => new TrackingClient("https://user:secret@example.com", "test"));
    }
    [Fact] public async Task NetworkFailureDoesNotCreateFallbackCoordinates()
    {
        using var client = new TrackingClient("https://example.com", "unit-test-token", new Handler(_ => throw new HttpRequestException("Offline fixture")));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLocationsAsync(Guid.NewGuid()));
    }
    [Fact] public async Task RevokedTokenErrorDoesNotLogResponseSecrets()
    {
        using var client = new TrackingClient("https://example.com", "unit-test-token", new Handler(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"code\":\"Unauthorized\",\"secret\":\"must-not-appear\"}") }));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLocationsAsync(Guid.NewGuid())); Assert.Contains("Unauthorized", ex.Message); Assert.DoesNotContain("must-not-appear", ex.Message);
    }
    [Fact] public void DesktopCredentialIsDpapiProtectedAndRemovable()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "credential.dpapi");
        try { var store = new DesktopCredentialStore(path); store.Save("https://example.com", "plain-unit-test-token"); Assert.DoesNotContain("plain-unit-test-token", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path))); Assert.Equal("plain-unit-test-token", store.Load()!.Value.Token); store.Delete(); Assert.Null(store.Load()); }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
