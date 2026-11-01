using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BLEStudio.Core;

namespace BLEStudio.Infrastructure;

public sealed class TrackingClient : ITrackingProvider, IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public Uri BaseUri { get; }
    public TrackingClient(string url, string accessToken, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new FormatException("The location service must use an absolute public HTTPS URL without embedded credentials, query or fragment.");
        BaseUri = uri; _http = handler == null ? new(new HttpClientHandler { AllowAutoRedirect = false }) : new(handler);
        _http.BaseAddress = uri; _http.Timeout = TimeSpan.FromSeconds(100);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BLEStudio/1.1");
    }
    public async Task<TrackerIdentity> RegisterAsync(TrackerIdentity identity, byte[] secret, CancellationToken token = default)
    {
        using var response = await _http.PostAsJsonAsync($"api/trackers/{identity.Id:D}/register", new { identity.Id, identity.Name, protocol = identity.Protocol.ToString(), identity.PairDate, identity.PublicKeyHex, secretBase64 = Convert.ToBase64String(secret), identity.CanonicId }, token);
        await RequireSuccess(response, token);
        var registered = await response.Content.ReadFromJsonAsync<TrackerIdentity>(Json, token) ?? throw new FormatException("Empty tracker registration.");
        return identity with { CanonicId = registered.CanonicId };
    }
    public async Task<TrackingResult> GetLocationsAsync(Guid trackerId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/trackers/{trackerId:D}/locations", cancellationToken);
        await RequireSuccess(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<TrackingResult>(cancellationToken: cancellationToken) ?? throw new FormatException("Empty tracking response.");
        if (result.Reports.Any(x => x.TrackerId != trackerId || !x.IsValid || !x.CryptographicallyVerified)) throw new FormatException("Service returned an invalid or unverified location report.");
        return result;
    }
    public async Task RefreshAsync(Guid trackerId, CancellationToken token = default)
    {
        using var response = await _http.PostAsync($"api/trackers/{trackerId:D}/refresh", null, token); await RequireSuccess(response, token);
    }
    private static async Task RequireSuccess(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        // Only a controlled public error code is read. Provider exceptions can contain account secrets.
        string code = "ServiceFailure";
        try { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); if (json.RootElement.TryGetProperty("code", out var c)) code = c.GetString() ?? code; } catch (JsonException) { }
        throw new HttpRequestException($"Tracking service HTTP {(int)response.StatusCode}: {code}.", null, response.StatusCode);
    }
    public void Dispose() => _http.Dispose();
}

public sealed class DesktopCredentialStore(string path)
{
    public void Save(string url, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { url, token })), null, DataProtectionScope.CurrentUser));
    }
    public (string Url, string Token)? Load()
    {
        if (!File.Exists(path)) return null;
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { var v = JsonSerializer.Deserialize<string[]>(clear)!; return (v[0], v[1]); } finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public void Delete() => File.Delete(path);
}
