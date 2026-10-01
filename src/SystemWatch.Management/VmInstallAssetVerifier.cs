using System.Security.Cryptography;

namespace SystemWatch.Management;

/// <summary>Read-only check of a staged three-file input set for later install planning.</summary>
public sealed class VmInstallAssetVerifier(VmInstallAssetProfile? profile = null)
{
    private readonly VmInstallAssetProfile? _profile = profile;

    public IReadOnlyList<VerifiedInstallAsset> Verify(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("asset directory must be an absolute, real directory");
        var selected = _profile ?? ReadReleaseProfile(directory);
        var expected = new[] { "SystemWatch.Worker", "system-watch-ui.js", "system-watch-proxmox.service" };
        var actual = Directory.EnumerateFileSystemEntries(directory)
            .Select(path => Path.GetFileName(path) ?? "")
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var entries = _profile is null ? expected.Append("release-inputs.json") : expected;
        if (!actual.SequenceEqual(entries.OrderBy(name => name, StringComparer.Ordinal)))
            throw new InvalidDataException("asset directory does not contain exactly the three reviewed inputs");
        return [
            VerifyFile(directory, expected[0], selected.WorkerHash, 256L * 1024 * 1024),
            VerifyFile(directory, expected[1], selected.UiAssetHash, 2L * 1024 * 1024),
            VerifyFile(directory, expected[2], selected.UnitTemplateHash, 8192),
        ];
    }

    private static VmInstallAssetProfile ReadReleaseProfile(string directory)
    {
        var path = Path.Combine(directory, "release-inputs.json");
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            new FileInfo(path).Length is < 1 or > 8192)
            throw new InvalidDataException("release input manifest is linked, empty or too large");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        string Get(string name)
        {
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                root.EnumerateObject().Count(p => p.NameEquals(name)) != 1 ||
                !root.TryGetProperty(name, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String)
                throw new InvalidDataException("invalid release input field: " + name);
            return value.GetString()!;
        }
        if (!root.TryGetProperty("format", out var format) || format.GetInt32() != 1)
            throw new InvalidDataException("unsupported release input format");
        var revision = Get("sourceRevision");
        var hashes = new[] { Get("workerSha256"), Get("uiSha256"), Get("unitSha256") };
        if (revision.Length != 40 || !revision.All(IsHex) ||
            hashes.Any(hash => hash.Length != 64 || !hash.All(IsHex)))
            throw new InvalidDataException("release input hash or source revision invalid");
        return new VmInstallAssetProfile(revision, hashes[0], hashes[1], hashes[2]);
    }

    private static bool IsHex(char ch) => ch is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static VerifiedInstallAsset VerifyFile(string directory, string name, string expected, long limit)
    {
        var path = Path.Combine(directory, name);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("asset is a directory or link: " + name);
        var bytes = new FileInfo(path).Length;
        if (bytes == 0 || bytes > limit)
            throw new InvalidDataException("asset size outside allowed range: " + name);
        using var input = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(input));
        if (hash != expected) throw new InvalidDataException("asset hash differs: " + name);
        return new VerifiedInstallAsset(name, hash, bytes);
    }
}
