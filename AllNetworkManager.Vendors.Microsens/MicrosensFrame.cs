namespace AllNetworkManager.Vendors.Microsens;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

/// <summary>Everything a MICROSENS switch tells us when it announces itself.</summary>
public sealed class MicrosensAnnounce
{
    public required byte[] Raw { get; init; }
    public int FrameVersion { get; init; }
    public bool Sectioned { get; init; }

    public string MacAddress { get; init; } = "";
    public string IpAddress { get; init; } = "";
    public string SubnetMask { get; init; } = "";
    public string Gateway { get; init; } = "";
    public int DhcpMode { get; init; }
    public string Hostname { get; init; } = "";
    public string Location { get; init; } = "";
    public string Group { get; init; } = "";
    public string ArticleNumber { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string HardwareVersion { get; init; } = "";
    public string FirmwareVersion { get; init; } = "";
    public int HttpsPort { get; init; }
    public string SaltSetting { get; init; } = "";
    public byte[]? IpSection { get; init; }
}

public record MicrosensOutgoing(byte[] Frame, int Port, bool Broadcast);

/// <summary>
/// MICROSENS proprietary switch IP protocol, reverse engineered from MsSwitchIpConfig.jar.
/// <para>
/// Two generations exist: legacy 92-byte frames starting with 04 F2 D7 5C (answer on UDP 8340, set IP on 8341,
/// no authentication) and "sectioned" frames starting with 0x59 (0x79 in replies) on UDP 1025 that carry
/// length-prefixed sections and an MD5/SHA-512 checksum plus an authenticated credential section.
/// </para>
/// </summary>
public static class MicrosensFrame
{
    public const int LegacyMagic0 = 0x04;
    public const int LegacyMagic1 = 0xF2;
    public const int LegacyMagic2 = 0xD7;
    public const int LegacyMagic3 = 0x5C;

    public const int SectionedId = 0x59;
    public const int SectionedPort = 1025;
    public const int LegacySetPort = 8341;
    public const int DiscoveryPort = 8340;
    public const int BootpPort = 67;

    public static bool LooksLikeMicrosens(byte[] data)
    {
        if (data.Length < 4) return false;

        if (data[0] == LegacyMagic0 && data[1] == LegacyMagic1 && data[2] == LegacyMagic2 && data[3] == LegacyMagic3)
            return true;

        return data[0] is SectionedId or 0x79 && data.Length >= 12;
    }

    public static bool TryParse(byte[] data, out MicrosensAnnounce? announce)
    {
        announce = null;
        if (!LooksLikeMicrosens(data)) return false;

        try
        {
            announce = data[0] == LegacyMagic0 ? ParseLegacy(data) : ParseSectioned(data);
            return announce != null;
        }
        catch
        {
            return false;
        }
    }

    private static MicrosensAnnounce? ParseSectioned(byte[] data)
    {
        int fv = data[1] & 0x7F;
        if (fv < 2) return null;

        bool sha512 = fv >= 4;
        int checksumLength = sha512 ? 64 : 16;
        int sizeOffset = fv >= 5 ? 50 : 10;
        int firstSection = fv >= 5 ? 52 : 12;

        if (data.Length < firstSection + checksumLength) return null;

        // The size field covers the whole frame including the trailing checksum.
        int totalSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(sizeOffset, 2));
        int frameSize = totalSize - checksumLength;
        if (frameSize < firstSection || totalSize > data.Length) return null;

        var payload = data.AsSpan(0, frameSize);
        var expected = data.AsSpan(frameSize, checksumLength);
        var actual = sha512 ? SHA512.HashData(payload) : MD5.HashData(payload);

        if (!actual.AsSpan().SequenceEqual(expected)) return null;

        var sections = ReadSections(data, firstSection, frameSize);
        if (!sections.TryGetValue(0, out var ipSection)) return null;

        string mac = ipSection.Length >= 8 ? FormatMac(ipSection.AsSpan(2, 6)) : "";
        int dhcpMode = ipSection.Length > 8 ? ipSection[8] : 0;
        string ip = ipSection.Length >= 13 ? IpFrom(ipSection, 9) : "0.0.0.0";
        string gateway = ipSection.Length >= 17 ? IpFrom(ipSection, 13) : "0.0.0.0";
        string mask = ipSection.Length >= 21 ? IpFrom(ipSection, 17) : "0.0.0.0";
        string hostname = ipSection.Length > 21 ? ReadLengthPrefixed(ipSection, 21) : "";
        int httpsPort = ipSection.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(ipSection.AsSpan(0, 2)) : 0;

        string article = "", serial = "", hw = "", fwVer = "";
        if (sections.TryGetValue(1, out var deviceSection) && deviceSection.Length > 8)
        {
            int pos = 8; // 4 bytes project number + 4 reserved
            article = ReadLengthPrefixed(deviceSection, ref pos);
            serial = ReadLengthPrefixed(deviceSection, ref pos);
            hw = ReadLengthPrefixed(deviceSection, ref pos);
            fwVer = ReadLengthPrefixed(deviceSection, ref pos);
        }

        string name = hostname, location = "", group = "";
        if (sections.TryGetValue(2, out var userSection))
        {
            int pos = 0;
            var userName = ReadLengthPrefixed(userSection, ref pos);
            if (!string.IsNullOrEmpty(userName)) name = userName;
            location = ReadLengthPrefixed(userSection, ref pos);
            group = ReadLengthPrefixed(userSection, ref pos);
        }

        var salt = fv >= 5 && data.Length >= 50
            ? Encoding.UTF8.GetString(data, 10, 40).TrimEnd('\0').Trim()
            : "";

        return new MicrosensAnnounce
        {
            Raw = data,
            FrameVersion = fv,
            Sectioned = true,
            MacAddress = mac,
            IpAddress = ip,
            SubnetMask = mask,
            Gateway = gateway,
            DhcpMode = dhcpMode,
            Hostname = name,
            Location = location,
            Group = group,
            ArticleNumber = article,
            SerialNumber = serial,
            HardwareVersion = hw,
            FirmwareVersion = fwVer,
            HttpsPort = httpsPort,
            SaltSetting = salt,
            IpSection = ipSection
        };
    }

    private static MicrosensAnnounce? ParseLegacy(byte[] data)
    {
        if (data.Length < 92) return null;

        string ip = $"{data[8]}.{data[9]}.{data[10]}.{data[11]}";
        string mask = $"{data[12]}.{data[13]}.{data[14]}.{data[15]}";
        string gateway = $"{data[16]}.{data[17]}.{data[18]}.{data[19]}";
        string mac = FormatMac(data.AsSpan(20, 6));
        string article = Encoding.ASCII.GetString(data, 26, 9).TrimEnd('\0');
        string serial = Encoding.ASCII.GetString(data, 35, 8).TrimEnd('\0');
        string name = Encoding.UTF8.GetString(data, 43, 25).TrimEnd('\0').Trim();
        string extArticle = Encoding.ASCII.GetString(data, 71, 21).TrimEnd('\0');

        if (article.Contains("xxx", StringComparison.OrdinalIgnoreCase)) article = extArticle;

        return new MicrosensAnnounce
        {
            Raw = data,
            FrameVersion = data[1] & 0x7F,
            Sectioned = false,
            MacAddress = mac,
            IpAddress = ip,
            SubnetMask = mask,
            Gateway = gateway,
            DhcpMode = data[68],
            Hostname = name,
            ArticleNumber = article,
            SerialNumber = serial,
            IpSection = null
        };
    }

    /// <summary>
    /// Builds the frame that sets the IP configuration of the announcing device.
    /// Sectioned devices answer on UDP 1025 with an authenticated credential section; legacy devices take the
    /// short fixed-layout frame on UDP 8341 with no authentication.
    /// </summary>
    public static MicrosensOutgoing BuildSetIp(
        MicrosensAnnounce announce,
        string ipAddress,
        string subnetMask,
        string gateway,
        string hostname,
        int dhcpMode,
        string password,
        int? randomSeed = null)
    {
        if (!announce.Sectioned) return BuildLegacySetIp(announce, ipAddress, subnetMask, gateway, hostname, dhcpMode);

        if (announce.IpSection == null || announce.IpSection.Length < 21)
            throw new InvalidOperationException("Announce frame has no usable IP section");

        int fv = announce.FrameVersion;
        bool sha512 = fv >= 4;
        int checksumLength = sha512 ? 64 : 16;

        // ---- header -------------------------------------------------------
        var header = new byte[fv <= 4 ? 12 : 52];
        header[0] = fv >= 3 ? (byte)0x79 : (byte)0x59;
        header[1] = (byte)(announce.Raw[1] | 0x80);
        Array.Copy(announce.Raw, 2, header, 2, 4);

        var random = new byte[4];
        if (randomSeed.HasValue) BinaryPrimitives.WriteInt32BigEndian(random, randomSeed.Value);
        else RandomNumberGenerator.Fill(random);
        Array.Copy(random, 0, header, 6, 4);

        if (fv >= 5) Array.Copy(announce.Raw, 10, header, 10, 40);

        // ---- section 0: IP -------------------------------------------------
        var ipPayload = (byte[])announce.IpSection.Clone();
        ipPayload[8] = (byte)dhcpMode;
        WriteIp(ipPayload, 9, ipAddress);
        WriteIp(ipPayload, 13, gateway);
        WriteIp(ipPayload, 17, subnetMask);

        var sectionIp = WrapSection(0, ipPayload);

        // ---- section 2: user (name / location / group) ---------------------
        var userPayload = new List<byte>();
        AddLengthPrefixed(userPayload, hostname);
        AddLengthPrefixed(userPayload, announce.Location);
        AddLengthPrefixed(userPayload, announce.Group);
        var sectionUser = WrapSection(2, userPayload.ToArray());

        // ---- section 3: credentials ----------------------------------------
        var sectionCredentials = new byte[3 + checksumLength];
        sectionCredentials[0] = 3;
        sectionCredentials[1] = 0;
        sectionCredentials[2] = (byte)checksumLength;

        int randomValue = BinaryPrimitives.ReadInt32BigEndian(random);
        string secret = fv switch
        {
            >= 5 => $"{announce.MacAddress}_{randomValue}_{MicrosensCrypt.Sha512Crypt(password, announce.SaltSetting)}",
            4 => password,
            _ => $"{announce.MacAddress}_{randomValue}_{MicrosensCrypt.Md5Crypt(password)}"
        };

        var secretDigest = sha512
            ? SHA512.HashData(Encoding.UTF8.GetBytes(secret))
            : MD5.HashData(Encoding.UTF8.GetBytes(secret));
        Array.Copy(secretDigest, 0, sectionCredentials, 3, checksumLength);

        // ---- assemble ------------------------------------------------------
        int frameSize = header.Length + sectionIp.Length + sectionUser.Length + sectionCredentials.Length;
        int sizeField = frameSize + checksumLength;
        int sizeOffset = fv <= 4 ? 10 : 50;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(sizeOffset, 2), (ushort)sizeField);

        var yFrame = new byte[frameSize];
        int offset = 0;
        foreach (var part in new[] { header, sectionIp, sectionUser, sectionCredentials })
        {
            Array.Copy(part, 0, yFrame, offset, part.Length);
            offset += part.Length;
        }

        var digest = sha512 ? SHA512.HashData(yFrame) : MD5.HashData(yFrame);
        var frame = new byte[frameSize + checksumLength];
        Array.Copy(yFrame, frame, yFrame.Length);
        Array.Copy(digest, 0, frame, yFrame.Length, checksumLength);

        // In-range devices can be addressed directly; the vendor tool falls back to broadcast otherwise.
        bool broadcast = fv >= 3
                         || announce.IpAddress is "0.0.0.0" or "255.255.255.255";

        return new MicrosensOutgoing(frame, SectionedPort, broadcast);
    }

    private static MicrosensOutgoing BuildLegacySetIp(
        MicrosensAnnounce announce, string ipAddress, string subnetMask, string gateway, string hostname, int dhcpMode)
    {
        var raw = announce.Raw;
        var frame = new byte[92];
        frame[0] = 0x36;
        frame[1] = 0xBD;
        frame[2] = 0xC1;
        frame[3] = 0x9A;

        Array.Copy(raw, 4, frame, 4, 4);           // version bytes
        WriteIp(frame, 8, ipAddress);
        WriteIp(frame, 12, subnetMask);
        WriteIp(frame, 16, gateway);
        Array.Copy(raw, 20, frame, 20, 6);         // MAC
        WriteAscii(frame, 26, 9, announce.ArticleNumber);
        WriteAscii(frame, 35, 8, announce.SerialNumber);
        WriteAscii(frame, 43, 25, hostname);
        frame[68] = (byte)(dhcpMode == 1 ? 1 : 0);
        Array.Copy(raw, 69, frame, 69, 2);

        var ext = raw.Length >= 92 ? Encoding.ASCII.GetString(raw, 71, 21).TrimEnd('\0') : "";
        WriteAscii(frame, 71, 21, ext);

        return new MicrosensOutgoing(frame, LegacySetPort, true);
    }

    /// <summary>Three-byte broadcast that asks switches to drop their IP and announce again.</summary>
    public static byte[] BuildDhcpRelease() => new byte[] { 0x00, 0x05, 0x00 };

    // ---------------------------------------------------------------- helpers

    private static Dictionary<int, byte[]> ReadSections(byte[] data, int start, int frameSize)
    {
        var sections = new Dictionary<int, byte[]>();
        int pos = start;

        while (pos + 3 <= frameSize)
        {
            int code = data[pos];
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 1, 2));
            int payloadStart = pos + 3;

            if (length <= 0 || payloadStart + length > frameSize) break;

            var payload = new byte[length];
            Array.Copy(data, payloadStart, payload, 0, length);
            sections[code] = payload;

            pos = payloadStart + length;
        }

        return sections;
    }

    private static byte[] WrapSection(int code, byte[] payload)
    {
        var section = new byte[3 + payload.Length];
        section[0] = (byte)code;
        BinaryPrimitives.WriteUInt16BigEndian(section.AsSpan(1, 2), (ushort)payload.Length);
        Array.Copy(payload, 0, section, 3, payload.Length);
        return section;
    }

    private static void WriteIp(byte[] buffer, int offset, string ip)
    {
        var parts = ip.Split('.');
        for (int i = 0; i < 4; i++)
        {
            buffer[offset + i] = byte.TryParse(parts.ElementAtOrDefault(i), out var value) ? value : (byte)0;
        }
    }

    private static string IpFrom(byte[] buffer, int offset)
        => $"{buffer[offset]}.{buffer[offset + 1]}.{buffer[offset + 2]}.{buffer[offset + 3]}";

    private static void WriteAscii(byte[] buffer, int offset, int length, string value)
    {
        Array.Clear(buffer, offset, length);
        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length));
    }

    private static void AddLengthPrefixed(List<byte> target, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        target.Add((byte)Math.Min(bytes.Length, 255));
        target.AddRange(bytes);
    }

    private static string ReadLengthPrefixed(byte[] payload, int offset)
    {
        int pos = offset;
        return ReadLengthPrefixed(payload, ref pos);
    }

    private static string ReadLengthPrefixed(byte[] payload, ref int pos)
    {
        if (pos >= payload.Length) return "";

        int length = payload[pos++];
        if (length <= 0 || pos + length > payload.Length) return "";

        var text = Encoding.UTF8.GetString(payload, pos, length).TrimEnd('\0').Trim();
        pos += length;
        return text;
    }

    private static string FormatMac(ReadOnlySpan<byte> mac)
        => string.Join("-", mac.ToArray().Select(b => b.ToString("X2")));
}
