using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SystemWatch.Collector;
using SystemWatch.Management;

var repo = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var fixtures = Path.Combine(repo, "tests/fixtures/vm-patches");
var staging = Path.Combine(Path.GetTempPath(), "system-watch-recipes-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(staging);
try
{
    var backendOriginal = File.ReadAllBytes(Path.Combine(fixtures, "backend-original.txt"));
    var frontendOriginal = File.ReadAllBytes(Path.Combine(fixtures, "frontend-original.js"));
    var expectedBackend = File.ReadAllBytes(Path.Combine(fixtures, "backend-expected.txt"));
    var expectedFrontend = File.ReadAllBytes(Path.Combine(fixtures, "frontend-expected.js"));
    static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL " + label);
        Console.WriteLine("PASS " + label);
    }
    static void Reject(Action action, string label)
    {
        try { action(); }
        catch (InvalidDataException) { Console.WriteLine("PASS " + label); return; }
        throw new Exception("FAIL " + label);
    }
    var profile = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "profiles/pve-manager-9.2.20.json")))!;
    var files = profile["files"]!.AsArray();
    files[0]!["originalSha256"] = Hash(backendOriginal);
    files[0]!["candidateSha256"] = Hash(expectedBackend);
    files[1]!["originalSha256"] = Hash(frontendOriginal);
    files[1]!["candidateSha256"] = Hash(expectedFrontend);
    File.Copy(Path.Combine(repo, "profiles/system-watch-method.pm.txt"),
        Path.Combine(staging, "system-watch-method.pm.txt"));
    File.Copy(Path.Combine(repo, "profiles/system-watch-bridge.js"),
        Path.Combine(staging, "system-watch-bridge.js"));
    var profilePath = Path.Combine(staging, "profile.json");
    void Save() => File.WriteAllText(profilePath, profile.ToJsonString());
    byte[] Original(string path) => path.EndsWith("Nodes.pm", StringComparison.Ordinal)
        ? backendOriginal : frontendOriginal;
    Save();
    var results = VmPatchRecipe.Render(profilePath, Original);
    Check(results.Count == 2 && results[0].Bytes.SequenceEqual(expectedBackend) &&
        results[1].Bytes.SequenceEqual(expectedFrontend), "recipe matches reviewed reference patchers byte for byte");
    profile["pveManagerVersion"] = "10.0.1";
    profile["id"] = "pve-manager-10.0.1-external-ui";
    Save();
    Check(VmPatchRecipe.Render(profilePath, Original).Count == 2,
        "numeric future recipe version uses the existing renderer without recompilation");
    profile["pveManagerVersion"] = "9.2.20";
    profile["id"] = "pve-manager-9.2.20-external-ui";
    profile["review"] = "draft";
    files[1]!["candidateSha256"] = null;
    Save();
    Reject(() => VmPatchRecipe.Render(profilePath, Original), "draft profile cannot authorize installation");
    var draft = VmPatchRecipe.Render(profilePath, Original, reviewDraft: true);
    Check(draft[1].CandidateSha256 == Hash(expectedFrontend), "read-only draft review renders candidate hash");
    profile["review"] = "reference-reviewed";
    files[1]!["candidateSha256"] = new string('0', 64);
    Save();
    Reject(() => VmPatchRecipe.Render(profilePath, Original), "wrong whole-file candidate hash refused");
    files[1]!["candidateSha256"] = Hash(expectedFrontend);
    files[1]!["originalSha256"] = new string('0', 64);
    Save();
    Reject(() => VmPatchRecipe.Render(profilePath, Original), "unknown original file refused");
    files[1]!["originalSha256"] = Hash(frontendOriginal);
    Save();
    var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(frontendOriginal) +
        "Ext.define('PVE.node.Summary', {");
    files[1]!["originalSha256"] = Hash(duplicate);
    Save();
    Reject(() => VmPatchRecipe.Render(profilePath, path => path.EndsWith("Nodes.pm", StringComparison.Ordinal)
        ? backendOriginal : duplicate), "ambiguous component boundary refused");
    files[1]!["originalSha256"] = Hash(frontendOriginal);
    files[1]!["path"] = "/tmp/unexpected.js";
    Save();
    Reject(() => VmPatchRecipe.Render(profilePath, Original), "unapproved target path refused");
    if (OperatingSystem.IsLinux())
    {
        files[1]!["path"] = "/usr/share/pve-manager/js/pvemanagerlib.js";
        Save();
        var hostRoot = Path.Combine(staging, "host");
        var package = Path.Combine(staging, "package");
        Directory.CreateDirectory(package);
        void Put(string absolute, byte[] bytes)
        {
            var destination = Path.Combine(hostRoot, absolute.TrimStart('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
        }
        Put("/usr/share/perl5/PVE/API2/Nodes.pm", backendOriginal);
        Put("/usr/share/pve-manager/js/pvemanagerlib.js", frontendOriginal);
        File.SetUnixFileMode(Path.Combine(hostRoot, "usr/share/perl5/PVE/API2/Nodes.pm"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(Path.Combine(hostRoot, "usr/share/pve-manager/js/pvemanagerlib.js"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Directory.CreateDirectory(Path.Combine(hostRoot, "etc/systemd/system"));
        var worker = Encoding.UTF8.GetBytes("synthetic worker");
        var ui = Encoding.UTF8.GetBytes("synthetic UI");
        var template = Encoding.UTF8.GetBytes("synthetic unit template");
        File.WriteAllBytes(Path.Combine(package, "SystemWatch.Worker"), worker);
        File.WriteAllBytes(Path.Combine(package, "system-watch-ui.js"), ui);
        File.WriteAllBytes(Path.Combine(package, "system-watch-proxmox.service"), template);
        var assets = new VmInstallAssetVerifier(new VmInstallAssetProfile(
            "fixture", Hash(worker), Hash(ui), Hash(template)));
        var runner = new RecipeFixtureRunner();
        var installer = new VmRecipeInstaller(hostRoot, runner, assets,
            (_, _) => Encoding.UTF8.GetBytes("synthetic rendered unit"),
            (_, _) => Task.CompletedTask);
        const string version = "pve-manager: 9.2.20 (fixture)\n";
        Check(installer.Status("node-example", version, profilePath) == "clean",
            "clean host recognized before installation");
        Reject(() => { _ = installer.Status("node-example", version, "profile.json"); },
            "status rejects a relative recipe path before inspecting vendor files");
        files[1]!["candidateSha256"] = new string('0', 64);
        Save();
        Check(installer.Status("node-example", version, profilePath).Contains(
            "candidate", StringComparison.Ordinal), "status preserves the recipe validation error");
        files[1]!["candidateSha256"] = Hash(expectedFrontend);
        Save();
        var productionPlan = await installer.PlanAsync("node-production", version, profilePath, package);
        Check(productionPlan.State.Host == "node-production", "recipe plan accepts explicitly reviewed node-production without writing");
        var testVmPlan = await installer.PlanAsync("node-fixture", version, profilePath, package);
        Check(testVmPlan.State.Host == "node-fixture", "recipe plan accepts explicitly reviewed node-fixture without writing");
        var planned = await installer.PlanAsync("node-example", version, profilePath, package);
        Check(planned.State.Files.Length == 5 && planned.Patches[1].Bytes.SequenceEqual(expectedFrontend),
            "read-only installer proposal records the reviewed candidates");
        await installer.InstallAsync("node-example", version, profilePath, package);
        Check(installer.Status("node-example", version, profilePath).StartsWith("file-complete", StringComparison.Ordinal),
            "both originals backed up and all install files recognized");
        var installedUi = Path.Combine(hostRoot, "usr/share/pve-manager/js/system-watch-ui.js");
        var updatedUi = Encoding.UTF8.GetBytes("synthetic two-panel UI");
        var updatedAsset = Path.Combine(staging, "updated-ui.js");
        File.WriteAllBytes(updatedAsset, updatedUi);
        Reject(() => { _ = installer.PlanUiUpdate("node-example", version, profilePath,
            updatedAsset, new string('0', 64)); }, "UI update refuses unreviewed asset hash");
        var uiPlan = installer.PlanUiUpdate("node-example", version, profilePath, updatedAsset, Hash(updatedUi));
        Check(uiPlan.Current.Files[2].CandidateSha256 == Hash(ui) &&
            uiPlan.Updated.Files[2].CandidateSha256 == Hash(updatedUi) &&
            File.ReadAllBytes(installedUi).SequenceEqual(ui),
            "read-only UI update records current and candidate hashes");
        var commandsBeforeUpdate = runner.Commands;
        installer.UpdateUi("node-example", version, profilePath, updatedAsset, Hash(updatedUi));
        Check(installer.Status("node-example", version, profilePath).StartsWith("file-complete", StringComparison.Ordinal) &&
            File.ReadAllBytes(installedUi).SequenceEqual(updatedUi) &&
            File.ReadAllBytes(Path.Combine(hostRoot, "usr/share/perl5/PVE/API2/Nodes.pm")).SequenceEqual(expectedBackend) &&
            File.ReadAllBytes(Path.Combine(hostRoot, "usr/share/pve-manager/js/pvemanagerlib.js")).SequenceEqual(expectedFrontend) &&
            runner.Commands == commandsBeforeUpdate,
            "UI-only update leaves vendor patches and services unchanged");
        Reject(() => { _ = installer.PlanUiUpdate("node-example", version, profilePath,
            updatedAsset, Hash(updatedUi)); }, "already installed UI asset refuses duplicate update");
        installer.UpdateUi("node-example", version, profilePath,
            Path.Combine(package, "system-watch-ui.js"), Hash(ui));
        Check(File.ReadAllBytes(installedUi).SequenceEqual(ui) &&
            installer.Status("node-example", version, profilePath).StartsWith("file-complete", StringComparison.Ordinal),
            "previous reviewed UI asset restores through the same update command");
        installer.UpdateUi("node-example", version, profilePath, updatedAsset, Hash(updatedUi));
        File.WriteAllText(installedUi, "unknown UI");
        Reject(() => { _ = installer.PlanUiUpdate("node-example", version, profilePath,
            Path.Combine(package, "system-watch-ui.js"), Hash(ui)); },
            "unknown installed UI blocks guarded update");
        File.WriteAllBytes(installedUi, updatedUi);
        var workerPath = Path.Combine(hostRoot, "opt/system-watch-proxmox/worker/SystemWatch.Worker");
        var newWorkerPath = Path.Combine(staging, "updated-worker");
        var newWorker = Encoding.UTF8.GetBytes("synthetic independently polling worker");
        File.WriteAllBytes(newWorkerPath, newWorker);
        Reject(() => { _ = installer.PlanWorkerUpdate("node-example", version, profilePath,
            newWorkerPath, Hash(worker)); }, "worker update refuses unreviewed bytes");
        var commandsBeforeWorkerPlan = runner.Commands;
        var workerPlan = installer.PlanWorkerUpdate("node-example", version, profilePath, newWorkerPath, Hash(newWorker));
        Check(workerPlan.Updated.Files[3].CandidateSha256 == Hash(newWorker) &&
            File.ReadAllBytes(workerPath).SequenceEqual(worker) && runner.Commands == commandsBeforeWorkerPlan,
            "worker plan records reviewed candidate without changing files or services");
        await installer.UpdateWorkerAsync("node-example", version, profilePath, newWorkerPath, Hash(newWorker));
        Check(File.ReadAllBytes(workerPath).SequenceEqual(newWorker) &&
            installer.Status("node-example", version, profilePath).StartsWith("file-complete", StringComparison.Ordinal) &&
            File.ReadAllBytes(installedUi).SequenceEqual(updatedUi) &&
            File.ReadAllBytes(Path.Combine(hostRoot, "usr/share/perl5/PVE/API2/Nodes.pm")).SequenceEqual(expectedBackend) &&
            File.ReadAllBytes(Path.Combine(hostRoot, "usr/share/pve-manager/js/pvemanagerlib.js")).SequenceEqual(expectedFrontend),
            "worker-only update preserves GUI and vendor patches with consistent manifest");
        Reject(() => { _ = installer.PlanWorkerUpdate("node-example", version, profilePath,
            newWorkerPath, Hash(newWorker)); }, "duplicate worker update refused");
        runner.FailNextWorkerStart = true;
        try
        {
            await installer.UpdateWorkerAsync("node-example", version, profilePath,
                Path.Combine(package, "SystemWatch.Worker"), Hash(worker));
            throw new Exception("failed worker start accepted");
        }
        catch (IOException) { }
        Check(File.ReadAllBytes(workerPath).SequenceEqual(newWorker) && runner.WorkerRunning &&
            installer.Status("node-example", version, profilePath).StartsWith("file-complete", StringComparison.Ordinal),
            "failed worker start restores previous binary and manifest and starts previous service");
        File.WriteAllText(workerPath, "unexpected worker bytes");
        Reject(() => { _ = installer.PlanWorkerUpdate("node-example", version, profilePath,
            Path.Combine(package, "SystemWatch.Worker"), Hash(worker)); }, "unknown installed worker blocks update");
        File.WriteAllBytes(workerPath, newWorker);
        var installedBackend = Path.Combine(hostRoot, "usr/share/perl5/PVE/API2/Nodes.pm");
        var manifestPath = Path.Combine(hostRoot, "var/lib/system-watch-proxmox/simple-install.json");
        // Model the earlier public-preview host slot without changing its worker.
        var legacyWorker = Path.Combine(hostRoot, "opt/system-watch-proxmox/csharp-fb6ee16/SystemWatch.Worker");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyWorker)!);
        File.Move(workerPath, legacyWorker);
        Directory.Delete(Path.GetDirectoryName(workerPath)!);
        workerPath = legacyWorker;
        var legacyManifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        legacyManifest["Files"]![3]!["Path"] = "/opt/system-watch-proxmox/csharp-fb6ee16/SystemWatch.Worker";
        File.WriteAllText(manifestPath, legacyManifest.ToJsonString(new() { WriteIndented = true }) + "\n");
        var oldManifest = File.ReadAllBytes(manifestPath);
        var upgradedOriginal = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(frontendOriginal) + "\n// upgraded vendor build\n");
        var upgradedCandidate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(expectedFrontend) + "\n// upgraded vendor build\n");
        var newProfile = JsonNode.Parse(File.ReadAllText(profilePath))!;
        newProfile["pveManagerVersion"] = "9.2.21";
        newProfile["id"] = "pve-manager-9.2.21-external-ui";
        newProfile["files"]![1]!["originalSha256"] = Hash(upgradedOriginal);
        newProfile["files"]![1]!["candidateSha256"] = Hash(upgradedCandidate);
        var newProfilePath = Path.Combine(staging, "upgraded-profile.json");
        File.WriteAllText(newProfilePath, newProfile.ToJsonString());
        const string upgradedVersion = "pve-manager: 9.2.21 (fixture)\n";
        File.WriteAllBytes(installedBackend, backendOriginal);
        File.WriteAllBytes(Path.Combine(hostRoot, "usr/share/pve-manager/js/pvemanagerlib.js"), upgradedOriginal);
        var oldBackupPath = Path.Combine(hostRoot, "var/lib/system-watch-proxmox/original-pvemanagerlib.js");
        var repairPlan = await installer.PlanRepairAsync("node-example", upgradedVersion, newProfilePath, profilePath);
        Check(repairPlan.Updated.Version == "9.2.21" && repairPlan.Updated.Files[3].Path.EndsWith("csharp-fb6ee16/SystemWatch.Worker", StringComparison.Ordinal) &&
            File.ReadAllBytes(manifestPath).SequenceEqual(oldManifest) && File.ReadAllBytes(oldBackupPath).SequenceEqual(frontendOriginal) &&
            File.ReadAllBytes(installedBackend).SequenceEqual(backendOriginal) &&
            !Directory.Exists(Path.Combine(hostRoot, "var/lib/system-watch-proxmox-history")),
            "repair plan recognizes overwritten hooks and earlier slot without changing files");
        File.WriteAllBytes(installedBackend, expectedBackend);
        Check((await installer.PlanRepairAsync("node-example", upgradedVersion, newProfilePath, profilePath)).NewOriginals[0].SequenceEqual(backendOriginal),
            "surviving recorded backend hook reuses only the matching original");
        File.WriteAllText(installedBackend, "unknown vendor bytes");
        Reject(() => installer.PlanRepairAsync("node-example", upgradedVersion, newProfilePath, profilePath).GetAwaiter().GetResult(),
            "repair refuses unknown vendor bytes");
        File.WriteAllBytes(installedBackend, backendOriginal);
        File.WriteAllText(installedUi, "unknown GUI");
        Reject(() => installer.PlanRepairAsync("node-example", upgradedVersion, newProfilePath, profilePath).GetAwaiter().GetResult(),
            "repair refuses changed project asset");
        File.WriteAllBytes(installedUi, updatedUi);
        File.WriteAllText(oldBackupPath, "unknown backup");
        Reject(() => installer.PlanRepairAsync("node-example", upgradedVersion, newProfilePath, profilePath).GetAwaiter().GetResult(),
            "repair refuses changed previous backup");
        File.WriteAllBytes(oldBackupPath, frontendOriginal);
        runner.FailNextIntegrationRestart = true;
        try
        {
            await installer.RepairAsync("node-example", upgradedVersion, newProfilePath, profilePath);
            throw new Exception("failed repair restart accepted");
        }
        catch (IOException) { }
        Check(File.ReadAllBytes(installedBackend).SequenceEqual(backendOriginal) &&
            File.ReadAllBytes(Path.Combine(hostRoot, "usr/share/pve-manager/js/pvemanagerlib.js")).SequenceEqual(upgradedOriginal) &&
            File.ReadAllBytes(oldBackupPath).SequenceEqual(frontendOriginal) && File.ReadAllBytes(manifestPath).SequenceEqual(oldManifest),
            "failed repair restores pre-repair state without restoring older vendor files");
        await installer.RepairAsync("node-example", upgradedVersion, newProfilePath, profilePath);
        Check(installer.Status("node-example", upgradedVersion, newProfilePath).StartsWith("file-complete", StringComparison.Ordinal) &&
            File.ReadAllBytes(oldBackupPath).SequenceEqual(upgradedOriginal) && File.ReadAllBytes(installedUi).SequenceEqual(updatedUi) &&
            File.ReadAllBytes(workerPath).SequenceEqual(newWorker),
            "repair refreshes vendor backups and manifest while preserving worker and GUI");
        var histories = Directory.GetDirectories(Path.Combine(hostRoot, "var/lib/system-watch-proxmox-history"));
        Check(histories.Length == 2 && histories.All(dir => File.ReadAllBytes(Path.Combine(dir, "simple-install.json")).SequenceEqual(oldManifest) &&
            File.ReadAllBytes(Path.Combine(dir, "original-pvemanagerlib.js")).SequenceEqual(frontendOriginal)),
            "repair retains private prior-version recovery evidence");
        Reject(() => installer.PlanRepairAsync("node-example", upgradedVersion, newProfilePath, newProfilePath).GetAwaiter().GetResult(),
            "complete repair refuses duplicate application");
        var installedFrontend = Path.Combine(hostRoot, "usr/share/pve-manager/js/pvemanagerlib.js");
        File.WriteAllText(installedFrontend, "unexpected change");
        Reject(() => installer.RemoveAsync("node-example", upgradedVersion, newProfilePath).GetAwaiter().GetResult(),
            "remove refuses unknown installed frontend bytes");
        File.WriteAllBytes(installedFrontend, upgradedCandidate);
        await installer.RemoveAsync("node-example", upgradedVersion, newProfilePath);
        Check(!File.Exists(manifestPath) &&
            File.ReadAllBytes(installedFrontend).SequenceEqual(upgradedOriginal) &&
            !File.Exists(installedUi) &&
            File.Exists(Path.Combine(hostRoot, "var/lib/system-watch-proxmox/original-Nodes.pm")) &&
            File.Exists(Path.Combine(hostRoot, "var/lib/system-watch-proxmox/original-pvemanagerlib.js")),
            "remove after repair restores upgraded originals and retains refreshed backups");
        Check(!Directory.Exists(Path.GetDirectoryName(workerPath)), "remove cleans the recorded worker directory");
    }
    if (args.Length > 1)
    {
        var originalDirectory = Path.GetFullPath(args[1]);
        var actual = VmPatchRecipe.Render(Path.Combine(repo, "profiles/pve-manager-9.2.10.json"),
            target => File.ReadAllBytes(Path.Combine(originalDirectory,
                target.EndsWith("Nodes.pm", StringComparison.Ordinal) ? "Nodes.pm" : "pvemanagerlib.js")));
        Check(actual[0].CandidateSha256 == "0b2421e30e9c6d0ad2ad93ec9773ab6add2813706f6b324ac0f0ffa390f88e5a" &&
            actual[1].CandidateSha256 == "6654a22eb5e8e19ad19758adf4988f5aa8c737e08ae3edf9e3614749fd303298",
            "real node-example originals render the reviewed C# backend and external bridge hashes");
    }
}
finally { Directory.Delete(staging, recursive: true); }

sealed class RecipeFixtureRunner : ICommandRunner
{
    public int Commands { get; private set; }
    public bool FailNextWorkerStart { get; set; }
    public bool FailNextIntegrationRestart { get; set; }
    public bool WorkerRunning { get; private set; }

    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        bool acceptNonZeroExit = false)
    {
        Commands++;
        if (arguments.Count > 0 && arguments[0] == "restart" && FailNextIntegrationRestart)
        {
            FailNextIntegrationRestart = false;
            return Task.FromException<CommandResult>(new IOException("simulated integration restart failure"));
        }
        if (arguments.Count > 0 && arguments[0] is "stop" or "disable") WorkerRunning = false;
        if (arguments.Count > 0 && arguments[0] == "start")
        {
            if (FailNextWorkerStart)
            {
                FailNextWorkerStart = false;
                return Task.FromException<CommandResult>(new IOException("simulated worker start failure"));
            }
            WorkerRunning = true;
        }
        var output = arguments.Count > 0 && arguments[0] == "show"
            ? "LoadState=not-found\nFragmentPath=\nDropInPaths=\nUnitFileState=\n"
            : arguments.Count > 0 && arguments[0] == "is-active" ? WorkerRunning ? "active\n" : "inactive\n" : "";
        return Task.FromResult(new CommandResult(output, "", 0));
    }
}
