namespace AllNetworkManager.Vendors.MikroTik;

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging;

public class RouterOsException : Exception
{
    public string Category { get; }

    public RouterOsException(string message, string category = "") : base(message) => Category = category;
}

/// <summary>
/// RouterOS API protocol (binary, sentence based). Reference: MikroTik "API" manual —
/// every sentence is a list of length-prefixed words terminated by a zero-length word;
/// replies are tagged <c>!re</c> / <c>!done</c> / <c>!trap</c> / <c>!fatal</c> with <c>=key=value</c> pairs.
/// </summary>
public sealed class RouterOsApiClient : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly TcpClient _tcp = new();
    private Stream? _stream;
    private bool _loggedIn;

    public bool IsConnected => _tcp.Connected && _stream != null;
    public bool IsSecure { get; private set; }
    public string Host { get; }
    public int Port { get; }

    public RouterOsApiClient(ILogger logger, string host, int port = 8728)
    {
        _logger = logger;
        Host = host;
        Port = port;
    }

    public async Task ConnectAsync(bool useTls, CancellationToken ct = default)
    {
        await _tcp.ConnectAsync(Host, Port, ct).ConfigureAwait(false);
        Stream stream = _tcp.GetStream();

        if (useTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = Host,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }, ct).ConfigureAwait(false);
            stream = ssl;
            IsSecure = true;
        }

        stream.ReadTimeout = 15000;
        stream.WriteTimeout = 15000;
        _stream = stream;
    }

    /// <summary>RouterOS 6.43+ accepts plain login; older versions need the MD5 challenge/response.</summary>
    public async Task LoginAsync(string username, string password, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(new[] { "/login", $"=name={username}", $"=password={password}" }, ct).ConfigureAwait(false);
            _loggedIn = true;
            return;
        }
        catch (RouterOsException ex) when (ex.Category is "login" or "" || ex.Message.Contains("log", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Plain login rejected ({Message}), trying MD5 challenge", ex.Message);
        }

        // RouterOS < 6.43: request challenge, then answer md5(0x00 + password + challenge)
        var challenge = await SendAsync(new[] { "/login" }, ct).ConfigureAwait(false);
        var ret = challenge.FirstOrDefault(r => r.ContainsKey("ret"))?["ret"];
        if (string.IsNullOrEmpty(ret))
            throw new RouterOsException("Login rejected and no challenge returned", "login");

        var response = "00" + Md5Hex(Encoding.ASCII.GetBytes(password), ret);
        await SendAsync(new[] { "/login", $"=name={username}", $"=response={response}" }, ct).ConfigureAwait(false);
        _loggedIn = true;
    }

    private static string Md5Hex(byte[] password, string challengeHex)
    {
        var challenge = Convert.FromHexString(challengeHex);
        var buffer = new byte[1 + password.Length + challenge.Length];
        buffer[0] = 0;
        password.CopyTo(buffer, 1);
        challenge.CopyTo(buffer, 1 + password.Length);
        return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(buffer)).ToLowerInvariant();
    }

    public Task<List<Dictionary<string, string>>> SendAsync(string[] words, CancellationToken ct = default)
        => SendAsync(words, null, ct);

    public async Task<List<Dictionary<string, string>>> SendAsync(
        string[] words, string? tag, CancellationToken ct = default)
    {
        if (_stream == null) throw new RouterOsException("Not connected", "transport");

        var payload = new List<string>(words);
        if (!string.IsNullOrEmpty(tag)) payload.Add($".tag={tag}");
        await WriteSentenceAsync(payload, ct).ConfigureAwait(false);

        var rows = new List<Dictionary<string, string>>();

        while (!ct.IsCancellationRequested)
        {
            if (!await ReadSentenceAsync(ct).ConfigureAwait(false))
                throw new RouterOsException("Connection closed by device", "transport");

            var sentence = _lastSentence;
            if (sentence.Count == 0) continue;

            var type = sentence[0];
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 1; i < sentence.Count; i++)
            {
                var w = sentence[i];
                if (w.StartsWith('='))
                {
                    var body = w[1..];
                    var eq = body.IndexOf('=');
                    if (eq > 0) row[body[..eq]] = body[(eq + 1)..];
                }
                else if (w.StartsWith('.'))
                {
                    var body = w[1..];
                    var eq = body.IndexOf('=');
                    if (eq > 0) row[body[..eq]] = body[(eq + 1)..];
                }
            }

            if (!string.IsNullOrEmpty(tag) &&
                row.TryGetValue("tag", out var rowTag) &&
                !string.Equals(rowTag, tag, StringComparison.Ordinal))
                continue;

            switch (type)
            {
                case "!re":
                    rows.Add(row);
                    break;
                case "!done":
                    return rows;
                case "!trap":
                    var msg = row.GetValueOrDefault("message", "unknown trap");
                    var cat = row.GetValueOrDefault("category", "");
                    _logger.LogDebug("RouterOS trap: {Message} ({Category})", msg, cat);
                    throw new RouterOsException(msg, cat);
                case "!fatal":
                    throw new RouterOsException(row.GetValueOrDefault("message", "fatal error"), "fatal");
            }
        }

        return rows;
    }

    private List<string> _lastSentence = new();

    private async Task WriteSentenceAsync(IEnumerable<string> words, CancellationToken ct)
    {
        foreach (var word in words)
        {
            var bytes = Encoding.UTF8.GetBytes(word);
            await WriteLengthAsync(bytes.Length, ct).ConfigureAwait(false);
            await _stream!.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        await _stream!.WriteAsync(new byte[] { 0 }, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteLengthAsync(int length, CancellationToken ct)
    {
        var buffer = RouterOsLength.Encode(length);
        await _stream!.WriteAsync(buffer, ct).ConfigureAwait(false);
    }

    private async Task<bool> ReadSentenceAsync(CancellationToken ct)
    {
        var words = new List<string>();

        while (true)
        {
            var length = await ReadLengthAsync(ct).ConfigureAwait(false);
            if (length < 0) return false;
            if (length == 0)
            {
                _lastSentence = words;
                return true;
            }

            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await _stream!.ReadAsync(buffer.AsMemory(read, length - read), ct).ConfigureAwait(false);
                if (n == 0) return false;
                read += n;
            }

            words.Add(Encoding.UTF8.GetString(buffer));
        }
    }

    private async Task<int> ReadLengthAsync(CancellationToken ct)
    {
        var first = await ReadByteAsync(ct).ConfigureAwait(false);
        if (first < 0) return -1;

        if ((first & 0x80) == 0x00) return first;
        if ((first & 0xC0) == 0x80)
            return ((first & ~0xC0) << 8) + await ReadByteAsync(ct).ConfigureAwait(false);
        if ((first & 0xE0) == 0xC0)
            return ((first & ~0xE0) << 16)
                   + (await ReadByteAsync(ct).ConfigureAwait(false) << 8)
                   + await ReadByteAsync(ct).ConfigureAwait(false);
        if ((first & 0xF0) == 0xE0)
            return ((first & ~0xF0) << 24)
                   + (await ReadByteAsync(ct).ConfigureAwait(false) << 16)
                   + (await ReadByteAsync(ct).ConfigureAwait(false) << 8)
                   + await ReadByteAsync(ct).ConfigureAwait(false);

        var length = 0;
        for (int i = 0; i < 4; i++)
            length = (length << 8) + await ReadByteAsync(ct).ConfigureAwait(false);
        return length;
    }

    private async Task<int> ReadByteAsync(CancellationToken ct)
    {
        var one = new byte[1];
        var n = await _stream!.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
        return n == 0 ? -1 : one[0];
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_loggedIn && _stream != null)
                await WriteSentenceAsync(new[] { "/quit" }, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }

        try { _stream?.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
        _stream = null;
    }
}

/// <summary>RouterOS variable-length word encoding. Public so it can be unit-tested without a router.</summary>
public static class RouterOsLength
{
    public static byte[] Encode(int length) => length switch
    {
        < 0x80 => new[] { (byte)length },
        < 0x4000 => new[]
        {
            (byte)((length >> 8) | 0x80),
            (byte)(length & 0xFF)
        },
        < 0x200000 => new[]
        {
            (byte)((length >> 16) | 0xC0),
            (byte)((length >> 8) & 0xFF),
            (byte)(length & 0xFF)
        },
        < 0x10000000 => new[]
        {
            (byte)((length >> 24) | 0xE0),
            (byte)((length >> 16) & 0xFF),
            (byte)((length >> 8) & 0xFF),
            (byte)(length & 0xFF)
        },
        _ => new[]
        {
            (byte)0xF0,
            (byte)((length >> 24) & 0xFF),
            (byte)((length >> 16) & 0xFF),
            (byte)((length >> 8) & 0xFF),
            (byte)(length & 0xFF)
        }
    };

    /// <summary>Decodes one length prefix; returns bytes consumed.</summary>
    public static int Decode(ReadOnlySpan<byte> data, out int length)
    {
        if (data.Length == 0) { length = 0; return 0; }

        byte first = data[0];
        if ((first & 0x80) == 0x00) { length = first; return 1; }
        if ((first & 0xC0) == 0x80) { length = ((first & ~0xC0) << 8) + data[1]; return 2; }
        if ((first & 0xE0) == 0xC0) { length = ((first & ~0xE0) << 16) + (data[1] << 8) + data[2]; return 3; }
        if ((first & 0xF0) == 0xE0)
        {
            length = ((first & ~0xF0) << 24) + (data[1] << 16) + (data[2] << 8) + data[3];
            return 4;
        }

        length = (data[1] << 24) + (data[2] << 16) + (data[3] << 8) + data[4];
        return 5;
    }
}
