namespace AllNetworkManager.Discovery.Protocols.Mdns;

using System.Net;
using System.Text;

/// <summary>Minimal DNS message reader/writer for mDNS (RFC 6762) and DNS-SD (RFC 6763).</summary>
public static class MdnsPacket
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeAaaa = 28;
    public const ushort TypeSrv = 33;
    public const ushort TypeAny = 255;

    public const ushort ClassIn = 1;
    /// <summary>"unicast response requested" bit in the question class.</summary>
    public const ushort QBit = 0x8000;

    public sealed record Record(
        string Name,
        ushort Type,
        uint Ttl,
        string? TargetName,
        ushort Port,
        IReadOnlyList<string> TextEntries,
        IPAddress? Address);

    public sealed record Message(IReadOnlyList<Record> Answers, IReadOnlyList<Record> Additional);

    /// <summary>Builds a PTR query for the given service names. <paramref name="unicastResponse"/> sets the QU bit.</summary>
    public static byte[] BuildPtrQuery(IEnumerable<string> services, bool unicastResponse = false)
        => BuildQuery(services, TypePtr, unicastResponse);

    /// <summary>Builds an A query for host names (second round of a browse).</summary>
    public static byte[] BuildAddressQuery(IEnumerable<string> hosts)
        => BuildQuery(hosts, TypeA, unicastResponse: true);

    private static byte[] BuildQuery(IEnumerable<string> names, ushort type, bool unicastResponse)
    {
        var questions = names.ToList();
        var buffer = new List<byte>(64);

        // header: id 0, flags 0 (standard query), qdcount, no answers/authority/additional
        buffer.AddRange(new byte[] { 0, 0, 0, 0 });
        buffer.Add((byte)(questions.Count >> 8));
        buffer.Add((byte)questions.Count);
        buffer.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });

        foreach (var name in questions)
        {
            WriteName(buffer, name);
            Add16(buffer, type);
            Add16(buffer, (ushort)(ClassIn | (unicastResponse ? QBit : 0)));
        }

        return buffer.ToArray();
    }

    /// <summary>Parses a reply; malformed packets yield an empty message instead of an exception.</summary>
    public static Message Parse(byte[] data)
    {
        var answers = new List<Record>();
        var additional = new List<Record>();

        if (data.Length < 12) return new Message(answers, additional);

        var answerCount = Read16(data, 6);
        var authorityCount = Read16(data, 8);
        var additionalCount = Read16(data, 10);
        var questionCount = Read16(data, 4);

        var position = 12;

        for (int i = 0; i < questionCount; i++)
        {
            ReadName(data, ref position, out _);
            position += 4; // qtype + qclass
            if (position > data.Length) return new Message(answers, additional);
        }

        for (int i = 0; i < answerCount + authorityCount + additionalCount; i++)
        {
            if (!ReadRecord(data, ref position, out var record)) break;

            if (i < answerCount) answers.Add(record);
            else additional.Add(record);
        }

        return new Message(answers, additional);
    }

    private static bool ReadRecord(byte[] data, ref int position, out Record record)
    {
        record = null!;

        if (!ReadName(data, ref position, out var name)) return false;
        if (position + 10 > data.Length) return false;

        var type = Read16(data, position);
        var klass = Read16(data, position + 2);
        var ttl = Read32(data, position + 4);
        var length = Read16(data, position + 8);
        position += 10;

        if (position + length > data.Length) return false;

        var dataStart = position;
        position += length;

        switch (type)
        {
            case TypePtr:
            {
                var cursor = dataStart;
                if (!ReadName(data, ref cursor, out var target)) return false;
                record = new Record(name, type, ttl, target, 0, Array.Empty<string>(), null);
                return true;
            }
            case TypeSrv:
            {
                if (length < 7) return false;
                var priority = Read16(data, dataStart);
                var weight = Read16(data, dataStart + 2);
                var port = Read16(data, dataStart + 4);
                var cursor = dataStart + 6;
                if (!ReadName(data, ref cursor, out var target)) return false;

                _ = priority;
                _ = weight;
                record = new Record(name, type, ttl, target, port, Array.Empty<string>(), null);
                return true;
            }
            case TypeTxt:
            {
                var entries = new List<string>();
                var cursor = dataStart;
                var end = dataStart + length;

                while (cursor < end)
                {
                    var entryLength = data[cursor++];
                    if (cursor + entryLength > end) break;

                    entries.Add(Encoding.UTF8.GetString(data, cursor, entryLength));
                    cursor += entryLength;
                }

                record = new Record(name, type, ttl, null, 0, entries, null);
                return true;
            }
            case TypeA:
            {
                if (length != 4) return false;
                record = new Record(name, type, ttl, null, 0, Array.Empty<string>(),
                    new IPAddress(data.AsSpan(dataStart, 4)));
                return true;
            }
            case TypeAaaa:
            {
                if (length != 16) return false;
                record = new Record(name, type, ttl, null, 0, Array.Empty<string>(),
                    new IPAddress(data.AsSpan(dataStart, 16)));
                return true;
            }
            default:
                record = new Record(name, type, ttl, null, 0, Array.Empty<string>(), null);
                return true;
        }
    }

    /// <summary>Reads a (possibly compressed) DNS name.</summary>
    private static bool ReadName(byte[] data, ref int position, out string name)
    {
        name = "";
        var labels = new List<string>();
        var cursor = position;
        var jumped = false;
        var guard = 0;

        while (cursor < data.Length && guard++ < 128)
        {
            var length = data[cursor];

            if (length == 0)
            {
                cursor++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= data.Length) return false;

                var pointer = (length & 0x3F) << 8 | data[cursor + 1];
                if (!jumped) position = cursor + 2;
                jumped = true;
                cursor = pointer;
                continue;
            }

            if (cursor + 1 + length > data.Length) return false;

            labels.Add(Encoding.UTF8.GetString(data, cursor + 1, length));
            cursor += 1 + length;
        }

        if (!jumped) position = cursor;
        name = string.Join('.', labels);
        return true;
    }

    private static void WriteName(List<byte> buffer, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63) continue;
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }

        buffer.Add(0);
    }

    private static void Add16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static ushort Read16(byte[] b, int o) => (ushort)(b[o] << 8 | b[o + 1]);

    private static uint Read32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
}

/// <summary>One service instance assembled from its PTR, SRV, TXT and A/AAAA records.</summary>
public sealed class MdnsServiceInstance
{
    public string InstanceName { get; set; } = "";
    public string Service { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public IPAddress? Address { get; set; }
    public Dictionary<string, string> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Looks a value up by any of the given TXT keys, case-insensitively.</summary>
    public string TxtValue(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (Txt.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return "";
    }

    public override string ToString() => $"{InstanceName} ({Service}) {Address}:{Port}";
}