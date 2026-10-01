using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SystemWatch.Management;

/// <summary>Exact, bounded renderer for locally reviewed Proxmox patch recipes.</summary>
public static class VmPatchRecipe
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] AllowedTargets = [
        "/usr/share/perl5/PVE/API2/Nodes.pm",
        "/usr/share/pve-manager/js/pvemanagerlib.js",
    ];

    public sealed record Candidate(string Path, string OriginalSha256, string CandidateSha256, byte[] Bytes);

    public static IReadOnlyList<Candidate> Render(string recipeFile,
        Func<string, byte[]> readOriginal, bool reviewDraft = false)
    {
        if (!Path.IsPathFullyQualified(recipeFile))
            throw new InvalidDataException("recipe path must be absolute");
        var attributes = File.GetAttributes(recipeFile);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            new FileInfo(recipeFile).Length > 65536)
            throw new InvalidDataException("recipe is linked, missing or too large");
        using var document = JsonDocument.Parse(File.ReadAllBytes(recipeFile), new JsonDocumentOptions {
            MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow,
        });
        var root = document.RootElement;
        if (Int(root, "format") != 2 ||
            String(root, "review") is not ("draft" or "reference-reviewed") ||
            !ValidVersion(String(root, "pveManagerVersion")))
            throw new InvalidDataException("unsupported recipe format or version");
        var version = String(root, "pveManagerVersion");
        if (String(root, "id") != $"pve-manager-{version}-external-ui")
            throw new InvalidDataException("recipe identity differs");
        var files = Array(root, "files");
        if (files.GetArrayLength() != AllowedTargets.Length)
            throw new InvalidDataException("recipe requires exactly two vendor targets");
        var output = new List<Candidate>();
        for (var index = 0; index < AllowedTargets.Length; index++)
        {
            var file = files[index];
            var target = String(file, "path");
            if (target != AllowedTargets[index])
                throw new InvalidDataException("unexpected vendor target: " + target);
            var before = String(file, "originalSha256");
            var after = OptionalString(file, "candidateSha256");
            if (!HashOk(before) || (after is not null && !HashOk(after)) ||
                (!reviewDraft && (after is null || String(root, "review") != "reference-reviewed")))
                throw new InvalidDataException("recipe lacks a reviewed original or candidate hash");
            var original = readOriginal(target);
            if (Hash(original) != before)
                throw new InvalidDataException("unknown original Proxmox file: " + target);
            var text = Utf8.GetString(original);
            var edits = Array(file, "edits");
            if (edits.GetArrayLength() is < 1 or > 16)
                throw new InvalidDataException("recipe edit count outside allowed range");
            foreach (var edit in edits.EnumerateArray())
            {
                var operation = String(edit, "op");
                var scoped = edit.TryGetProperty("region", out var region);
                var start = scoped ? String(region, "start") : null;
                var end = scoped ? String(region, "end") : null;
                var startAt = scoped ? Unique(text, start!) : 0;
                var endAt = scoped ? Unique(text, end!) : text.Length;
                if (scoped && endAt <= startAt + start!.Length)
                    throw new InvalidDataException("recipe region markers are out of order");
                if (operation == "insert-before-region-start")
                {
                    if (!scoped || target != AllowedTargets[1] ||
                        !String(edit, "textFile").Equals("system-watch-bridge.js", StringComparison.Ordinal) ||
                        !Boolean(edit, "trimTrailingWhitespace") || !Boolean(edit, "appendNewline"))
                        throw new InvalidDataException("unsupported bridge insertion");
                    text = text.Insert(startAt, Snippet(recipeFile, "system-watch-bridge.js").TrimEnd() + "\n");
                }
                else
                {
                    var find = String(edit, "find");
                    if (find.Length is < 10 or > 2048)
                        throw new InvalidDataException("unsafe recipe anchor");
                    var sectionStart = scoped ? startAt + start!.Length : 0;
                    var section = text.Substring(sectionStart, endAt - sectionStart);
                    var indexInSection = Unique(section, find);
                    var at = sectionStart + indexInSection;
                    if (operation == "replace-once")
                    {
                        var replacement = String(edit, "with");
                        if (replacement.Length > 8192) throw new InvalidDataException("replacement too large");
                        text = text[..at] + replacement + text[(at + find.Length)..];
                    }
                    else if (operation == "insert-before-once")
                    {
                        if (target != AllowedTargets[0] || String(edit, "textFile") != "system-watch-method.pm.txt")
                            throw new InvalidDataException("unsupported method insertion");
                        text = text.Insert(at, Snippet(recipeFile, "system-watch-method.pm.txt"));
                    }
                    else throw new InvalidDataException("unknown recipe edit operation");
                }
            }
            var candidate = Utf8.GetBytes(text);
            var actual = Hash(candidate);
            if (after is not null && actual != after)
                throw new InvalidDataException("candidate differs from reviewed output: " + target);
            output.Add(new Candidate(target, before, actual, candidate));
        }
        return output;
    }

    private static string Snippet(string recipeFile, string name)
    {
        var path = Path.Combine(Path.GetDirectoryName(recipeFile)!, name);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            new FileInfo(path).Length is <= 0 or > 65536)
            throw new InvalidDataException("recipe snippet is linked or too large");
        return Utf8.GetString(File.ReadAllBytes(path));
    }

    private static int Unique(string text, string find)
    {
        if (find.Length == 0) throw new InvalidDataException("empty recipe anchor");
        var at = text.IndexOf(find, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(find, at + find.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("expected one exact recipe anchor: " + find[..Math.Min(find.Length, 70)]);
        return at;
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            element.EnumerateObject().Count(property => property.NameEquals(name)) != 1)
            throw new InvalidDataException("missing or repeated recipe property: " + name);
        return value;
    }
    private static string String(JsonElement element, string name) =>
        Property(element, name).ValueKind == JsonValueKind.String
            ? Property(element, name).GetString()! : throw new InvalidDataException("invalid recipe property: " + name);
    private static string? OptionalString(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind switch {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new InvalidDataException("invalid recipe property: " + name),
        };
    }
    private static int Int(JsonElement element, string name) => Property(element, name).GetInt32();
    private static bool Boolean(JsonElement element, string name) => Property(element, name).GetBoolean();
    private static JsonElement Array(JsonElement element, string name)
    {
        var array = Property(element, name);
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("invalid recipe array: " + name);
        return array;
    }
    internal static bool ValidVersion(string value) => value.Length is >= 5 and <= 32 &&
        value.Split('.').Length == 3 && value.Split('.').All(part =>
            part.Length is >= 1 and <= 6 && part.All(ch => ch is >= '0' and <= '9'));

    private static bool HashOk(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
