namespace AllNetworkManager.Vendors.Wisenet;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using AllNetworkManager.Core.Devices;
using Microsoft.Extensions.Logging;

public class SunapiException : Exception
{
    public int StatusCode { get; }

    public SunapiException(string message, int statusCode = -1) : base(message) => StatusCode = statusCode;
}

public record SunapiStatus(bool Success, int Code, string Message);

/// <summary>
/// SUNAPI (Samsung/Hanwha Unified Network API) client: HTTP GET/POST against
/// <c>/stw-cgi/&lt;service&gt;.cgi?msubmenu=&lt;menu&gt;&amp;action=&lt;action&gt;</c>.
/// Auth is HTTP Digest (or Basic on newer firmware); the handler negotiates it from the credentials.
/// Responses are XML; control actions answer with a statusCode where 0 means OK.
/// </summary>
public class SunapiClient : IDisposable
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly Uri _base;

    public Uri BaseAddress => _base;

    public SunapiClient(ILogger logger, string ipAddress, int? port, DeviceCredentials credentials)
    {
        _logger = logger;

        // newer Wisenet firmware disables http and answers only on 443 with a self-signed cert
        var useHttps = port == 443;
        var scheme = useHttps ? "https" : "http";
        var portPart = port is > 0 and not 80 and not 443 ? $":{port}" : "";
        _base = new Uri($"{scheme}://{ipAddress}{portPart}/stw-cgi/");

        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(credentials.Username, credentials.Password),
            PreAuthenticate = true,
            AllowAutoRedirect = true
        };

        if (useHttps)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AllNetworkManager/1.0");
    }

    public Task<XDocument> ViewAsync(string service, string menu, IDictionary<string, string>? extra = null, CancellationToken ct = default)
        => RequestAsync(service, menu, "view", extra, ct);

    private async Task<SunapiStatus> StatusAsync(
        string service, string menu, string action, IDictionary<string, string>? extra, CancellationToken ct)
        => ParseStatus(await RequestAsync(service, menu, action, extra, ct).ConfigureAwait(false));

    public Task<SunapiStatus> SetAsync(string service, string menu, IDictionary<string, string>? extra = null, CancellationToken ct = default)
        => StatusAsync(service, menu, "set", extra, ct);

    public Task<SunapiStatus> AddAsync(string service, string menu, IDictionary<string, string>? extra = null, CancellationToken ct = default)
        => StatusAsync(service, menu, "add", extra, ct);

    public Task<SunapiStatus> RemoveAsync(string service, string menu, IDictionary<string, string>? extra = null, CancellationToken ct = default)
        => StatusAsync(service, menu, "remove", extra, ct);

    public Task<SunapiStatus> ControlAsync(string service, string menu, IDictionary<string, string>? extra = null, CancellationToken ct = default)
        => StatusAsync(service, menu, "control", extra, ct);

    public async Task<SunapiStatus> UploadAsync(string url, HttpContent content, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("SUNAPI upload {Url} -> {Status}: {Body}", url, response.StatusCode, Truncate(text));
            return new SunapiStatus(false, (int)response.StatusCode, Truncate(text));
        }

        return ParseStatus(SafeParse(text));
    }

    private async Task<XDocument> RequestAsync(
        string service, string menu, string action, IDictionary<string, string>? extra, CancellationToken ct)
    {
        var query = new StringBuilder($"{service}.cgi?msubmenu={Uri.EscapeDataString(menu)}&action={Uri.EscapeDataString(action)}&View=Allowed");
        if (extra != null)
        {
            foreach (var kv in extra)
                query.Append($"&{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
        }

        var url = new Uri(_base, query.ToString());
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SunapiException($"SUNAPI auth failed ({(int)response.StatusCode}) on {service}.cgi/{menu}", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
            throw new SunapiException($"SUNAPI {service}.cgi/{menu} returned {(int)response.StatusCode}", (int)response.StatusCode);

        var doc = SafeParse(text);
        if (doc.Root == null && string.IsNullOrWhiteSpace(text))
            return new XDocument(new XElement("empty"));

        return doc;
    }

    private XDocument SafeParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new XDocument(new XElement("empty"));

        try
        {
            return XDocument.Parse(text);
        }
        catch
        {
            _logger.LogDebug("SUNAPI response is not XML: {Body}", Truncate(text));
            return new XDocument(new XElement("plain", text));
        }
    }

    public static SunapiStatus ParseStatus(XDocument doc)
    {
        var codeText = Xml.FindValue(doc, "statusCode", "code", "result");
        var message = Xml.FindValue(doc, "statusString", "errorString", "message", "reason") ?? "";

        if (int.TryParse(codeText, out var code))
            return new SunapiStatus(code == 0, code, message);

        // Some control endpoints answer with a bare <OK/> or an empty body on success
        if (codeText == null) return new SunapiStatus(true, 0, message);

        return new SunapiStatus(false, -1, message.Length > 0 ? message : codeText);
    }

    private static string Truncate(string text) => text.Length > 300 ? text[..300] : text;

    public void Dispose() => _http.Dispose();
}

/// <summary>Tolerant XML reader: SUNAPI schemas differ between firmware generations and models.</summary>
public static class Xml
{
    public static string? FindValue(XDocument doc, params string[] names)
    {
        if (doc.Root == null) return null;

        foreach (var name in names)
        {
            // Leaf elements first: a container like <Version><Version>2.10</Version></Version> would
            // otherwise return every descendant's text concatenated.
            var matches = doc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var element = matches.FirstOrDefault(e => !e.HasElements && !string.IsNullOrWhiteSpace(e.Value))
                          ?? matches.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Value));

            if (element != null) return element.Value.Trim();

            var attribute = doc.Descendants()
                .SelectMany(e => e.Attributes())
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (attribute != null && !string.IsNullOrWhiteSpace(attribute.Value)) return attribute.Value.Trim();
        }

        return null;
    }

    public static IEnumerable<XElement> Blocks(XDocument doc, params string[] names)
    {
        if (doc.Root == null) return Array.Empty<XElement>();

        foreach (var name in names)
        {
            var list = doc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (list.Count > 0) return list;
        }

        // Fall back to the children of the root that have element children
        return doc.Root.Elements().Where(e => e.HasElements);
    }
}
