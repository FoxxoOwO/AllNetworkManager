namespace AllNetworkManager.Vendors.Axis;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AllNetworkManager.Core.Devices;
using Microsoft.Extensions.Logging;

public class VapixException : Exception
{
    public int StatusCode { get; }
    public string? Body { get; }

    public VapixException(string message, int statusCode = -1, string? body = null) : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }
}

/// <summary>
/// VAPIX client: HTTP(S) with Digest/Basic auth against <c>/axis-cgi/*</c>. Axis devices ship self-signed
/// certificates, so certificate validation is disabled — the device is reached by IP on the local network.
/// </summary>
public class VapixClient : IDisposable
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly Uri _base;

    public Uri BaseAddress => _base;

    public VapixClient(ILogger logger, string ipAddress, int? port, DeviceCredentials credentials)
    {
        _logger = logger;

        var useHttps = port is null or 443;
        var scheme = useHttps ? "https" : "http";
        var portPart = port is > 0 and not 443 and not 80 ? $":{port}" : "";

        _base = new Uri($"{scheme}://{ipAddress}{portPart}/");

        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(credentials.Username, credentials.Password),
            PreAuthenticate = true,
            AllowAutoRedirect = false
        };

        if (useHttps)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AllNetworkManager/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> GetStringAsync(string relativeUrl, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(new Uri(_base, relativeUrl), ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new VapixException($"VAPIX auth failed ({(int)response.StatusCode}) for {relativeUrl}", (int)response.StatusCode, body);

        if (!response.IsSuccessStatusCode)
            throw new VapixException($"VAPIX {relativeUrl} returned {(int)response.StatusCode}", (int)response.StatusCode, body);

        return body;
    }

    public async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken ct = default)
    {
        var body = await GetStringAsync(relativeUrl, ct).ConfigureAwait(false);

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug("VAPIX {Url} did not return JSON: {Body}", relativeUrl, Truncate(body));
            throw new VapixException($"VAPIX {relativeUrl} did not return JSON: {ex.Message}", 200, Truncate(body));
        }
    }

    public async Task<string> PostJsonAsync(string relativeUrl, string json, CancellationToken ct = default)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(new Uri(_base, relativeUrl), content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new VapixException($"VAPIX {relativeUrl} returned {(int)response.StatusCode}: {Truncate(body)}",
                (int)response.StatusCode, Truncate(body));

        return body;
    }

    /// <summary>Uploads a firmware image; Axis firmware updates take minutes, so this uses its own timeout.</summary>
    public async Task<string> UploadFirmwareAsync(string relativeUrl, string fileName, byte[] data, CancellationToken ct = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);

        using var response = await _http.PostAsync(new Uri(_base, relativeUrl), content, linked.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new VapixException($"VAPIX firmware upload returned {(int)response.StatusCode}: {Truncate(body)}",
                (int)response.StatusCode, Truncate(body));

        return body;
    }

    public async Task<byte[]> GetBytesAsync(string relativeUrl, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(new Uri(_base, relativeUrl), ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new VapixException($"VAPIX {relativeUrl} returned {(int)response.StatusCode}", (int)response.StatusCode);

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Parses the classic <c>key=value</c> parameter dump returned by param.cgi.</summary>
    public static Dictionary<string, string> ParseParamText(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;

            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;

            result[trimmed[..idx]] = trimmed[(idx + 1)..];
        }

        return result;
    }

    private static string Truncate(string text) => text.Length > 300 ? text[..300] : text;

    public void Dispose() => _http.Dispose();
}
