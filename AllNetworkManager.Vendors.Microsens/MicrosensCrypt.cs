namespace AllNetworkManager.Vendors.Microsens;

using CryptSharp;

/// <summary>
/// Credential hashing used by the MICROSENS switch IP protocol.
/// The vendor tool hashes its password two ways:
/// <list type="bullet">
/// <item>frame version &lt; 4: Unix MD5-crypt with the fixed salt "MSG60" ($1$MSG60$…)</item>
/// <item>frame version &gt;= 5: Unix SHA-512-crypt with the 40-byte salt string carried in the announce frame</item>
/// </list>
/// The security blob sent to the device is <c>mac + "_" + randomInt + "_" + hash</c>.
/// </summary>
public static class MicrosensCrypt
{
    public const string LegacySalt = "MSG60";

    /// <summary>Unix MD5-crypt, e.g. "administrator" -> $1$MSG60$blOXlZf.6D4OkSADOEEs31</summary>
    public static string Md5Crypt(string password, string salt = LegacySalt)
        => Crypter.MD5.Crypt(password, "$1$" + salt);

    /// <summary>Unix SHA-512-crypt against a full salt setting read from the announce frame ($6$…$ or bare salt).</summary>
    public static string Sha512Crypt(string password, string setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return Crypter.Sha512.Crypt(password);

        // CryptSharp wants the setting without the trailing '$' ("$6$MSG60"), the device sends "$6$MSG60$".
        var salt = setting.Trim();
        if (!salt.StartsWith("$6$", StringComparison.Ordinal)) salt = "$6$" + salt;
        salt = salt.TrimEnd('$');

        return Crypter.Sha512.Crypt(password, salt);
    }

    /// <summary>The hash the device expects for the given frame version.</summary>
    public static string HashForFrame(int frameVersion, string password, string saltFromFrame)
        => frameVersion >= 5 ? Sha512Crypt(password, saltFromFrame) : Md5Crypt(password);
}
