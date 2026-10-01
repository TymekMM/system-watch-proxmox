using System.Security.Cryptography;
using System.Text;

namespace SystemWatch.Management;

/// <summary>Bind the reviewed C# service template to an explicitly selected hostname.</summary>
public static class VmServiceCandidateRenderer
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string TemplateHash = "5b9e464f30d8c260c57c5c871137c606e1619d7215fb7c3fca859111839be73f";

    public static byte[] Render(ReadOnlySpan<byte> original, string expectedHostname)
    {
        if (expectedHostname.Length is < 2 or > 63 ||
            expectedHostname[0] is not (>= 'a' and <= 'z') ||
            expectedHostname[^1] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            !expectedHostname.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            throw new InvalidDataException("requires a valid explicitly selected hostname");
        if (Convert.ToHexStringLower(SHA256.HashData(original)) != TemplateHash)
            throw new InvalidDataException("unit template does not match the reviewed hash");
        var text = Utf8.GetString(original);
        const string placeholder = "@HOSTNAME@";
        var at = text.IndexOf(placeholder, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(placeholder, at + placeholder.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("unit requires exactly one hostname placeholder");
        return Utf8.GetBytes(text.Replace(placeholder, expectedHostname, StringComparison.Ordinal));
    }
}
