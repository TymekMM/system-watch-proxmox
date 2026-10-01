using System.Security.Cryptography;
using System.Text;
using SystemWatch.Management;

var root = Path.Combine(Path.GetTempPath(), "system-watch-assets-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL " + description);
        Console.WriteLine("PASS " + description);
    }
    static void Reject(Action action, string description)
    {
        try { action(); }
        catch (InvalidDataException) { Console.WriteLine("PASS " + description); return; }
        throw new Exception("FAIL " + description);
    }
    var profile = new VmInstallAssetProfile("fixture-revision", Hash("worker"), Hash("ui"), Hash("unit"));
    var verifier = new VmInstallAssetVerifier(profile);
    var worker = Path.Combine(root, "SystemWatch.Worker");
    var ui = Path.Combine(root, "system-watch-ui.js");
    var unit = Path.Combine(root, "system-watch-proxmox.service");
    File.WriteAllText(worker, "worker");
    File.WriteAllText(ui, "ui");
    File.WriteAllText(unit, "unit");
    var before = Directory.GetFiles(root).ToDictionary(x => x, File.ReadAllText);
    var assets = verifier.Verify(root);
    Check(assets.Select(x => x.Name).SequenceEqual([
        "SystemWatch.Worker", "system-watch-ui.js", "system-watch-proxmox.service"]),
        "exact staged asset set and stable manifest order");
    Check(assets[0].Sha256 == profile.WorkerHash && assets[0].Bytes == 6,
        "worker bytes and hash verified");
    File.WriteAllText(ui, "changed");
    Reject(() => verifier.Verify(root), "changed UI asset rejected");
    File.WriteAllText(ui, "ui");
    File.WriteAllText(Path.Combine(root, "extra"), "payload");
    Reject(() => verifier.Verify(root), "unreviewed extra file rejected");
    File.Delete(Path.Combine(root, "extra"));
    File.Delete(unit);
    Reject(() => verifier.Verify(root), "missing unit template rejected");
    File.WriteAllText(unit, "unit");
    File.Delete(worker);
    File.CreateSymbolicLink(worker, ui);
    Reject(() => verifier.Verify(root), "linked worker rejected");
    File.Delete(worker);
    File.WriteAllText(worker, "worker");
    Reject(() => verifier.Verify("relative/path"), "relative package directory rejected");
    var releaseManifest = Path.Combine(root, "release-inputs.json");
    var release = new { format = 1, version = "1.0.0-preview.1", sourceRevision = new string('a', 40),
        workerSha256 = Hash("worker"), uiSha256 = Hash("ui"), unitSha256 = Hash("unit") };
    File.WriteAllText(releaseManifest, System.Text.Json.JsonSerializer.Serialize(release));
    var packaged = new VmInstallAssetVerifier();
    Check(packaged.Verify(root).Count == 3, "release manifest verifies current package without historical binary pins");
    File.WriteAllText(worker, "different release bytes");
    Reject(() => packaged.Verify(root), "package manifest rejects changed worker bytes");
    File.WriteAllText(worker, "worker");
    File.WriteAllText(releaseManifest, System.Text.Json.JsonSerializer.Serialize(release with { sourceRevision = "bad" }));
    Reject(() => packaged.Verify(root), "malformed release source revision refused");
    File.Delete(releaseManifest);
    Check(verifier.Verify(root).Count == 3 && before.All(x => File.ReadAllText(x.Key) == x.Value),
        "verification leaves reviewed files unchanged");
}
finally
{
    Directory.Delete(root, recursive: true);
}
