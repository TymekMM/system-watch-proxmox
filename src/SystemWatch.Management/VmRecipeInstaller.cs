using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using SystemWatch.Collector;

namespace SystemWatch.Management;

/// <summary>Supervised clean-host install/remove using one reviewed patch recipe.</summary>
[SupportedOSPlatform("linux")]
public sealed class VmRecipeInstaller
{
    private const string Service = "system-watch-proxmox.service";
    private const string StateDirectory = "/var/lib/system-watch-proxmox";
    private const string StatePath = StateDirectory + "/simple-install.json";
    private const string BackendBackup = StateDirectory + "/original-Nodes.pm";
    private const string FrontendBackup = StateDirectory + "/original-pvemanagerlib.js";
    private const string Worker = "/opt/system-watch-proxmox/worker/SystemWatch.Worker";
    private const string Ui = "/usr/share/pve-manager/js/system-watch-ui.js";
    private const string Unit = "/etc/systemd/system/system-watch-proxmox.service";
    private static readonly UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PublicFile = PrivateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private static readonly UnixFileMode Executable = PrivateDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string root;
    private readonly ICommandRunner _runner;
    private readonly VmInstallAssetVerifier _verifier;
    private readonly Func<byte[], string, byte[]> _unit;
    private readonly Func<byte[], CancellationToken, Task> _perl;

    public VmRecipeInstaller(string root = "/", ICommandRunner? runner = null)
        : this(root, runner ?? new ProcessCommandRunner(4_194_304), new VmInstallAssetVerifier(),
            (template, host) => VmServiceCandidateRenderer.Render(template, host),
            VmPerlCandidateValidator.ValidateAsync) { }

    internal VmRecipeInstaller(string root, ICommandRunner runner, VmInstallAssetVerifier verifier,
        Func<byte[], string, byte[]> unit, Func<byte[], CancellationToken, Task> perl)
    {
        this.root = root;
        _runner = runner;
        _verifier = verifier;
        _unit = unit;
        _perl = perl;
    }

    public sealed record FileEntry(string Path, string CandidateSha256, string? OriginalSha256,
        string? BackupPath, string Mode);
    public sealed record Manifest(int Format, string Host, string Version, string RecipeSha256,
        FileEntry[] Files);
    public sealed record Proposal(Manifest State, IReadOnlyList<VmPatchRecipe.Candidate> Patches,
        byte[] Worker, byte[] Ui, byte[] Unit);
    public sealed record UiUpdate(Manifest Current, Manifest Updated, byte[] Previous,
        byte[] Candidate);

    public async Task<Proposal> PlanAsync(string hostname, string versionOutput,
        string profileFile, string assets, CancellationToken token = default)
    {
        RequireHost(hostname, versionOutput, profileFile);
        if (Exists(At(StatePath))) throw new InvalidDataException("existing installation manifest; run status");
        var patches = VmPatchRecipe.Render(profileFile, path => File.ReadAllBytes(At(path)));
        await _perl(patches[0].Bytes, token);
        var verified = _verifier.Verify(assets)
            .ToDictionary(asset => asset.Name, StringComparer.Ordinal);
        var worker = File.ReadAllBytes(Path.Combine(assets, "SystemWatch.Worker"));
        var ui = File.ReadAllBytes(Path.Combine(assets, "system-watch-ui.js"));
        var unit = _unit(
            File.ReadAllBytes(Path.Combine(assets, "system-watch-proxmox.service")), hostname);
        if (Hash(worker) != verified["SystemWatch.Worker"].Sha256 ||
            Hash(ui) != verified["system-watch-ui.js"].Sha256)
            throw new InvalidDataException("package asset changed after inspection");
        var entries = new[] {
            new FileEntry(patches[0].Path, patches[0].CandidateSha256,
                patches[0].OriginalSha256, BackendBackup, "0644"),
            new FileEntry(patches[1].Path, patches[1].CandidateSha256,
                patches[1].OriginalSha256, FrontendBackup, "0644"),
            new FileEntry(Ui, Hash(ui), null, null, "0644"),
            new FileEntry(Worker, Hash(worker), null, null, "0700"),
            new FileEntry(Unit, Hash(unit), null, null, "0644"),
        };
        var version = RecipeVersion(profileFile);
        var manifest = new Manifest(1, hostname, version, Hash(File.ReadAllBytes(profileFile)), entries);
        await CheckVacantAsync(manifest, token);
        return new Proposal(manifest, patches, worker, ui, unit);
    }

    public async Task InstallAsync(string hostname, string versionOutput, string profileFile,
        string assets, CancellationToken token = default)
    {
        if (root == "/" && Environment.UserName != "root")
            throw new UnauthorizedAccessException("install requires root");
        var proposal = await PlanAsync(hostname, versionOutput, profileFile, assets, token);
        BackupBoth(proposal);
        if (Hash(File.ReadAllBytes(profileFile)) != proposal.State.RecipeSha256)
            throw new InvalidDataException("recipe changed between plan and publication");
        Publish(StatePath, Serialize(proposal.State), PrivateFile, overwrite: false);
        try
        {
            Publish(Worker, proposal.Worker, Executable, overwrite: false);
            Publish(Ui, proposal.Ui, PublicFile, overwrite: false);
            Publish(Unit, proposal.Unit, PublicFile, overwrite: false);
            Publish(proposal.Patches[0].Path, proposal.Patches[0].Bytes, PublicFile, overwrite: true,
                proposal.Patches[0].OriginalSha256);
            Publish(proposal.Patches[1].Path, proposal.Patches[1].Bytes, PublicFile, overwrite: true,
                proposal.Patches[1].OriginalSha256);
            await Command("/usr/bin/systemctl", ["daemon-reload"], token);
            await Command("/usr/bin/systemctl", ["enable", "--now", Service], token);
            await Command("/usr/bin/systemctl", ["restart", "pvedaemon", "pveproxy"], token);
            if (root == "/") await VerifyLiveAsync(hostname, token);
        }
        catch
        {
            try { await RemoveAsync(hostname, versionOutput, profileFile, CancellationToken.None); }
            catch (Exception failure)
            {
                Console.Error.WriteLine("Automatic restore incomplete; inspect status: " + failure.Message);
            }
            throw;
        }
    }

    public string Status(string hostname, string versionOutput, string profileFile)
    {
        if (!Path.IsPathFullyQualified(profileFile))
            throw new InvalidDataException("recipe path must be absolute");
        RequireHost(hostname, versionOutput, profileFile);
        if (!Exists(At(StatePath)))
        {
            try { _ = VmPatchRecipe.Render(profileFile, path => File.ReadAllBytes(At(path))); }
            catch (InvalidDataException failure) { return "drift (recipe validation: " + failure.Message + ")"; }
            if (new[] { Ui, Worker, Unit, "/opt/system-watch-proxmox" }.Any(path => Exists(At(path))) ||
                Exists(At("/etc/systemd/system/system-watch-proxmox.service.d")))
                return "drift (project files exist without a manifest)";
            return ExistingBackups(profileFile) ? "clean (retained original backups)" :
                !Exists(At(StateDirectory)) ? "clean" : "drift (unknown state directory)";
        }
        var state = Load(hostname, profileFile);
        var (complete, detail) = InspectFiles(state);
        return detail is not null ? "drift (" + detail + ")" :
            complete ? "file-complete (check service, API and browser separately)" : "incomplete";
    }

    public UiUpdate PlanUiUpdate(string hostname, string versionOutput, string profileFile,
        string assetPath, string expectedSha256)
    {
        RequireHost(hostname, versionOutput, profileFile);
        if (!ValidHash(expectedSha256) || !Path.IsPathFullyQualified(assetPath))
            throw new InvalidDataException("UI update needs an absolute asset and a lowercase SHA256");
        var attributes = File.GetAttributes(assetPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            new FileInfo(assetPath).Length is < 1 or > 1_048_576)
            throw new InvalidDataException("UI update asset is linked, empty or too large");
        var candidate = File.ReadAllBytes(assetPath);
        if (Hash(candidate) != expectedSha256)
            throw new InvalidDataException("UI update asset differs from reviewed SHA256");
        var current = Load(hostname, profileFile);
        var (complete, drift) = InspectFiles(current);
        if (drift is not null || !complete)
            throw new InvalidDataException("installed files are not complete: " + (drift ?? "incomplete"));
        if (current.Files[2].CandidateSha256 == expectedSha256)
            throw new InvalidDataException("UI update already installed");
        var previous = File.ReadAllBytes(At(Ui));
        var files = (FileEntry[])current.Files.Clone();
        files[2] = files[2] with { CandidateSha256 = expectedSha256 };
        return new UiUpdate(current, current with { Files = files }, previous, candidate);
    }

    public void UpdateUi(string hostname, string versionOutput, string profileFile,
        string assetPath, string expectedSha256)
    {
        if (root == "/" && Environment.UserName != "root")
            throw new UnauthorizedAccessException("UI update requires root");
        var change = PlanUiUpdate(hostname, versionOutput, profileFile, assetPath, expectedSha256);
        var previousHash = change.Current.Files[2].CandidateSha256;
        if (Hash(File.ReadAllBytes(assetPath)) != expectedSha256)
            throw new InvalidDataException("UI update asset changed after plan");
        Publish(Ui, change.Candidate, PublicFile, overwrite: true, previousHash);
        try
        {
            Publish(StatePath, Serialize(change.Updated), PrivateFile, overwrite: true,
                Hash(Serialize(change.Current)));
        }
        catch
        {
            try { Publish(Ui, change.Previous, PublicFile, overwrite: true, expectedSha256); }
            catch (Exception failure)
            {
                Console.Error.WriteLine("UI asset restoration incomplete; inspect status: " + failure.Message);
            }
            throw;
        }
    }

    public VmWorkerUpdate PlanWorkerUpdate(string hostname, string versionOutput,
        string profileFile, string assetPath, string expectedSha256)
    {
        RequireHost(hostname, versionOutput, profileFile);
        if (!ValidHash(expectedSha256) || !Path.IsPathFullyQualified(assetPath) ||
            !File.Exists(assetPath) ||
            (File.GetAttributes(assetPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            new FileInfo(assetPath).Length is <= 0 or > 268435456)
            throw new InvalidDataException("worker update requires an absolute regular file and reviewed hash");
        var candidate = File.ReadAllBytes(assetPath);
        if (Hash(candidate) != expectedSha256)
            throw new InvalidDataException("worker update asset differs from reviewed SHA256");
        var current = Load(hostname, profileFile);
        var (complete, drift) = InspectFiles(current);
        if (!complete || drift is not null)
            throw new InvalidDataException("installed files are not complete: " + (drift ?? "incomplete"));
        if (current.Files[3].CandidateSha256 == expectedSha256)
            throw new InvalidDataException("worker update already installed");
        var files = (FileEntry[])current.Files.Clone();
        files[3] = files[3] with { CandidateSha256 = expectedSha256 };
        return new(current, current with { Files = files }, File.ReadAllBytes(At(current.Files[3].Path)), candidate);
    }

    public async Task UpdateWorkerAsync(string hostname, string versionOutput, string profileFile,
        string assetPath, string expectedSha256, CancellationToken token = default)
    {
        if (root == "/" && Environment.UserName != "root")
            throw new UnauthorizedAccessException("worker update requires root");
        var change = PlanWorkerUpdate(hostname, versionOutput, profileFile, assetPath, expectedSha256);
        string? previousInstance = null;
        if (root == "/")
        {
            var effective = await Command("/usr/bin/systemctl",
                ["show", Service, "--property=FragmentPath,DropInPaths,NeedDaemonReload", "--no-pager"], token);
            var lines = effective.Stdout.TrimEnd('\n', '\r').Split('\n');
            if (lines.Length != 3 || !lines.Contains("FragmentPath=" + Unit, StringComparer.Ordinal) ||
                !lines.Contains("DropInPaths=", StringComparer.Ordinal) ||
                !lines.Contains("NeedDaemonReload=no", StringComparer.Ordinal))
                throw new InvalidDataException("loaded service differs from the reviewed unit");
            var active = await Command("/usr/bin/systemctl", ["is-active", Service], token);
            if (active.Stdout.Trim() != "active") throw new InvalidDataException("worker update requires an active service");
            using var snapshot = JsonDocument.Parse(File.ReadAllBytes(At("/run/system-watch-proxmox/snapshot.json")));
            previousInstance = snapshot.RootElement.GetProperty("snapshot").GetProperty("instance_id").GetString();
            if (string.IsNullOrEmpty(previousInstance)) throw new InvalidDataException("current worker instance missing");
        }
        var binaryChanged = false;
        var manifestChanged = false;
        try
        {
            await Command("/usr/bin/systemctl", ["stop", Service], token);
            var stopped = await Command("/usr/bin/systemctl", ["is-active", Service], token, acceptNonZero: true);
            if (stopped.Stdout.Trim() != "inactive") throw new InvalidDataException("worker did not stop");
            var rechecked = PlanWorkerUpdate(hostname, versionOutput, profileFile, assetPath, expectedSha256);
            if (!Serialize(rechecked.Current).SequenceEqual(Serialize(change.Current)))
                throw new InvalidDataException("installation manifest changed after worker update plan");
            Publish(change.Current.Files[3].Path, change.Candidate, Executable, overwrite: true, change.Current.Files[3].CandidateSha256);
            binaryChanged = true;
            Publish(StatePath, Serialize(change.Updated), PrivateFile, overwrite: true, Hash(Serialize(change.Current)));
            manifestChanged = true;
            await Command("/usr/bin/systemctl", ["start", Service], token);
            if (root == "/") await VerifyLiveAsync(hostname, token, previousInstance);
        }
        catch
        {
            // Restore using a fresh token even if the caller canceled the update.
            try
            {
                await Command("/usr/bin/systemctl", ["stop", Service], CancellationToken.None);
                if (binaryChanged)
                    Publish(change.Current.Files[3].Path, change.Previous, Executable, overwrite: true, expectedSha256);
                if (manifestChanged)
                    Publish(StatePath, Serialize(change.Current), PrivateFile, overwrite: true, Hash(Serialize(change.Updated)));
                await Command("/usr/bin/systemctl", ["start", Service], CancellationToken.None);
                if (root == "/") await VerifyLiveAsync(hostname, CancellationToken.None);
                Console.Error.WriteLine("Previous worker and manifest restored.");
            }
            catch (Exception failure)
            {
                Console.Error.WriteLine("Worker restoration incomplete; inspect status: " + failure.Message);
            }
            throw;
        }
    }

    public async Task RemoveAsync(string hostname, string versionOutput, string profileFile,
        CancellationToken token = default)
    {
        if (root == "/" && Environment.UserName != "root")
            throw new UnauthorizedAccessException("remove requires root");
        RequireHost(hostname, versionOutput, profileFile);
        if (!Exists(At(StatePath)))
        {
            if (!ExistingBackups(profileFile)) throw new InvalidDataException("no recognized install to remove");
            return; // A backup-only interrupted step changed no installed targets.
        }
        var state = Load(hostname, profileFile);
        var (_, detail) = InspectFiles(state);
        if (detail is not null) throw new InvalidDataException("unknown installed state: " + detail);
        var unit = state.Files[4];
        if (Exists(At(unit.Path)))
        {
            if (root == "/")
            {
                var effective = await Command("/usr/bin/systemctl",
                    ["show", Service, "--property=FragmentPath,DropInPaths", "--no-pager"], token);
                var lines = effective.Stdout.TrimEnd('\n', '\r').Split('\n');
                if (lines.Length != 2 ||
                    !lines.Contains("FragmentPath=" + Unit, StringComparer.Ordinal) ||
                    !lines.Contains("DropInPaths=", StringComparer.Ordinal))
                    throw new InvalidDataException("effective service is not the reviewed project unit");
            }
            await Command("/usr/bin/systemctl", ["disable", "--now", Service], token);
            var inactive = await Command("/usr/bin/systemctl", ["is-active", Service], token, acceptNonZero: true);
            if (inactive.Stdout.Trim() == "active")
                throw new InvalidDataException("project service still active");
        }
        // The backups are fully checked above before either vendor file is restored.
        for (var i = 0; i < 2; i++)
        {
            var entry = state.Files[i];
            if (Hash(File.ReadAllBytes(At(entry.Path))) == entry.CandidateSha256)
                Publish(entry.Path, File.ReadAllBytes(At(entry.BackupPath!)), PublicFile,
                    overwrite: true, entry.CandidateSha256);
        }
        await Command("/usr/bin/systemctl", ["restart", "pvedaemon", "pveproxy"], token);
        for (var i = 4; i >= 2; i--)
        {
            var entry = state.Files[i];
            if (Exists(At(entry.Path))) DeleteKnown(entry.Path, entry.CandidateSha256);
        }
        await Command("/usr/bin/systemctl", ["daemon-reload"], token);
        File.Delete(At(StatePath));
        LinuxDirectorySync.Flush(At(StateDirectory));
        var workerDir = Path.GetDirectoryName(At(state.Files[3].Path))!;
        if (Directory.Exists(workerDir) && !Directory.EnumerateFileSystemEntries(workerDir).Any())
            Directory.Delete(workerDir);
        var projectDir = Path.GetDirectoryName(workerDir)!;
        if (Directory.Exists(projectDir) && !Directory.EnumerateFileSystemEntries(projectDir).Any())
            Directory.Delete(projectDir);
    }

    public async Task<VmRepairPlan> PlanRepairAsync(string hostname, string versionOutput,
        string profileFile, string previousProfile, CancellationToken token = default)
    {
        RequireHost(hostname, versionOutput, profileFile);
        var current = Load(hostname, previousProfile);
        if (!ExistingBackupsFromWithManifest(current.Files))
            throw new InvalidDataException("previous original backups missing or changed");
        foreach (var entry in current.Files.Skip(2))
            if (HashAt(entry.Path) != entry.CandidateSha256 ||
                File.GetUnixFileMode(At(entry.Path)) != (entry.Path == current.Files[3].Path ? Executable : PublicFile))
                throw new InvalidDataException("project file differs from manifest: " + entry.Path);
        var originals = new byte[2][];
        var before = new byte[2][];
        var backups = new byte[2][];
        using var recipe = JsonDocument.Parse(File.ReadAllBytes(profileFile));
        var targets = recipe.RootElement.GetProperty("files");
        if (targets.GetArrayLength() != 2) throw new InvalidDataException("repair needs two vendor targets");
        for (var i = 0; i < 2; i++)
        {
            var entry = current.Files[i];
            if (HashAt(entry.Path) is null || File.GetUnixFileMode(At(entry.Path)) != PublicFile)
                throw new InvalidDataException("vendor file missing, linked or wrong mode");
            before[i] = File.ReadAllBytes(At(entry.Path));
            backups[i] = File.ReadAllBytes(At(entry.BackupPath!));
            var expected = targets[i].GetProperty("originalSha256").GetString();
            // A surviving old hook is reusable only when its backed-up original is
            // exactly the new recipe's original. Never strip hooks from unknown bytes.
            originals[i] = Hash(before[i]) == expected ? before[i] :
                Hash(before[i]) == entry.CandidateSha256 && Hash(backups[i]) == expected ? backups[i] :
                throw new InvalidDataException("vendor bytes are neither new original nor a reusable recorded hook: " + entry.Path);
        }
        var patches = VmPatchRecipe.Render(profileFile, path =>
            originals[path == current.Files[0].Path ? 0 : 1]);
        await _perl(patches[0].Bytes, token);
        var files = (FileEntry[])current.Files.Clone();
        for (var i = 0; i < 2; i++)
            files[i] = files[i] with { CandidateSha256 = patches[i].CandidateSha256,
                OriginalSha256 = patches[i].OriginalSha256 };
        var updated = current with { Version = RecipeVersion(profileFile),
            RecipeSha256 = Hash(File.ReadAllBytes(profileFile)), Files = files };
        if (Serialize(updated).SequenceEqual(Serialize(current)) &&
            before.Select(Hash).SequenceEqual(patches.Select(patch => patch.CandidateSha256)))
            throw new InvalidDataException("hooks already match the recorded installation");
        if (Exists(At("/etc/systemd/system/system-watch-proxmox.service.d")))
            throw new InvalidDataException("repair refuses service drop-ins");
        if (root == "/")
        {
            var ownership = await Command("/usr/bin/stat", ["-c", "%u:%g", "--",
                At(StateDirectory), At(StatePath), At(BackendBackup), At(FrontendBackup),
                .. current.Files.Select(entry => At(entry.Path))], token);
            if (ownership.Stdout.TrimEnd('\n', '\r') != string.Join("\n", Enumerable.Repeat("0:0", 9)))
                throw new InvalidDataException("repair files must be root owned");
            var effective = await Command("/usr/bin/systemctl", ["show", Service,
                "--property=FragmentPath,DropInPaths,NeedDaemonReload", "--no-pager"], token);
            var lines = effective.Stdout.TrimEnd('\n', '\r').Split('\n');
            if (lines.Length != 3 || !lines.Contains("FragmentPath=" + Unit, StringComparer.Ordinal) ||
                !lines.Contains("DropInPaths=", StringComparer.Ordinal) ||
                !lines.Contains("NeedDaemonReload=no", StringComparer.Ordinal))
                throw new InvalidDataException("loaded project service differs from its recorded unit");
            var active = await Command("/usr/bin/systemctl", ["is-active", Service], token);
            if (active.Stdout.Trim() != "active") throw new InvalidDataException("repair requires an active worker");
        }
        return new(current, updated, patches, before, backups, originals);
    }

    public async Task RepairAsync(string hostname, string versionOutput, string profileFile,
        string previousProfile, CancellationToken token = default)
    {
        if (root == "/" && Environment.UserName != "root")
            throw new UnauthorizedAccessException("repair requires root");
        var change = await PlanRepairAsync(hostname, versionOutput, profileFile, previousProfile, token);
        var historyParent = StateDirectory + "-history";
        if (Exists(At(historyParent)) && (!Directory.Exists(At(historyParent)) ||
            (File.GetAttributes(At(historyParent)) & FileAttributes.ReparsePoint) != 0 ||
            File.GetUnixFileMode(At(historyParent)) != PrivateDirectory))
            throw new InvalidDataException("repair history directory is linked or has wrong mode");
        if (root == "/" && Exists(At(historyParent)))
        {
            var owner = await Command("/usr/bin/stat", ["-c", "%u:%g", "--", At(historyParent)], token);
            if (owner.Stdout.Trim() != "0:0") throw new InvalidDataException("repair history is not root owned");
        }
        Directory.CreateDirectory(At(historyParent), PrivateDirectory);
        File.SetUnixFileMode(At(historyParent), PrivateDirectory);
        LinuxDirectorySync.Flush(Path.GetDirectoryName(At(historyParent))!);
        var history = historyParent + "/" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(At(history), PrivateDirectory);
        LinuxDirectorySync.Flush(At(historyParent));
        Publish(history + "/simple-install.json", Serialize(change.Current), PrivateFile, false);
        Publish(history + "/repaired-install.json", Serialize(change.Updated), PrivateFile, false);
        for (var i = 0; i < 2; i++)
        {
            var name = i == 0 ? "Nodes.pm" : "pvemanagerlib.js";
            Publish(history + "/original-" + name, change.PreviousBackups[i], PrivateFile, false);
            Publish(history + "/before-" + name, change.BeforeVendor[i], PrivateFile, false);
            Publish(history + "/new-original-" + name, change.NewOriginals[i], PrivateFile, false);
        }
        Console.WriteLine("Repair recovery evidence retained: " + history);
        // Recheck all planned inputs after saving evidence and before changing active state.
        var rechecked = await PlanRepairAsync(hostname, versionOutput, profileFile, previousProfile, token);
        if (!Serialize(rechecked.Current).SequenceEqual(Serialize(change.Current)) ||
            !Serialize(rechecked.Updated).SequenceEqual(Serialize(change.Updated)) ||
            !rechecked.BeforeVendor.Select(Hash).SequenceEqual(change.BeforeVendor.Select(Hash)))
            throw new InvalidDataException("repair inputs changed after review");
        try
        {
            for (var i = 0; i < 2; i++)
                Publish(change.Current.Files[i].BackupPath!, change.NewOriginals[i], PrivateFile, true,
                    Hash(change.PreviousBackups[i]));
            Publish(StatePath, Serialize(change.Updated), PrivateFile, true, Hash(Serialize(change.Current)));
            for (var i = 0; i < 2; i++)
                if (Hash(change.BeforeVendor[i]) != change.Patches[i].CandidateSha256)
                    Publish(change.Current.Files[i].Path, change.Patches[i].Bytes, PublicFile, true,
                        Hash(change.BeforeVendor[i]));
            await Command("/usr/bin/systemctl", ["restart", "pvedaemon", "pveproxy"], token);
            if (root == "/") await VerifyLiveAsync(hostname, token);
        }
        catch
        {
            try
            {
                for (var i = 0; i < 2; i++)
                    RestoreRepairFile(change.Current.Files[i].Path, change.BeforeVendor[i],
                        change.Patches[i].CandidateSha256, PublicFile);
                for (var i = 0; i < 2; i++)
                    RestoreRepairFile(change.Current.Files[i].BackupPath!, change.PreviousBackups[i],
                        Hash(change.NewOriginals[i]), PrivateFile);
                RestoreRepairFile(StatePath, Serialize(change.Current), Hash(Serialize(change.Updated)), PrivateFile);
                await Command("/usr/bin/systemctl", ["restart", "pvedaemon", "pveproxy"], CancellationToken.None);
                Console.Error.WriteLine("Pre-repair files and manifest restored; upgraded vendor files retained.");
            }
            catch (Exception failure)
            {
                Console.Error.WriteLine("Repair restoration incomplete; use evidence at " + history + ": " + failure.Message);
            }
            throw;
        }
    }

    private void RestoreRepairFile(string path, byte[] before, string publishedHash, UnixFileMode mode)
    {
        var actual = HashAt(path);
        if (actual == Hash(before)) return;
        if (actual != publishedHash) throw new InvalidDataException("unknown bytes block repair restoration: " + path);
        Publish(path, before, mode, true, publishedHash);
    }

    private async Task CheckVacantAsync(Manifest plan, CancellationToken token)
    {
        foreach (var file in plan.Files.Take(2))
        {
            var target = At(file.Path);
            if ((File.GetAttributes(target) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                File.GetUnixFileMode(target) != PublicFile)
                throw new InvalidDataException("vendor file linked or has unexpected mode: " + file.Path);
        }
        if (root == "/")
        {
            var owners = await Command("/usr/bin/stat",
                ["-c", "%u:%g", "--", At(plan.Files[0].Path), At(plan.Files[1].Path)], token);
            if (owners.Stdout.TrimEnd('\n', '\r') != "0:0\n0:0")
                throw new InvalidDataException("vendor files are not root owned");
        }
        foreach (var file in plan.Files.Skip(2))
            if (Exists(At(file.Path))) throw new InvalidDataException("project install path occupied: " + file.Path);
        if (Exists(At("/opt/system-watch-proxmox")))
            throw new InvalidDataException("project program directory already occupied");
        if (Exists(At("/etc/systemd/system/system-watch-proxmox.service.d")))
            throw new InvalidDataException("service drop-in path occupied");
        if (Exists(At(StateDirectory)) && !ExistingBackupsFrom(plan.Files))
            throw new InvalidDataException("management directory already claimed");
        var loaded = await Command("/usr/bin/systemctl",
            ["show", Service, "--property=LoadState,FragmentPath,DropInPaths,UnitFileState", "--no-pager"], token);
        var fields = loaded.Stdout.TrimEnd('\n', '\r').Split('\n');
        if (fields.Length != 4 ||
            !fields.Contains("LoadState=not-found", StringComparer.Ordinal) ||
            !fields.Contains("FragmentPath=", StringComparer.Ordinal) ||
            !fields.Contains("DropInPaths=", StringComparer.Ordinal) ||
            !fields.Contains("UnitFileState=", StringComparer.Ordinal))
            throw new InvalidDataException("service name is already claimed");
    }

    private bool ExistingBackups(string profileFile)
    {
        if (!Directory.Exists(At(StateDirectory))) return false;
        var recipe = VmPatchRecipe.Render(profileFile, path => File.ReadAllBytes(At(path)), reviewDraft: true);
        return ExistingBackupsFrom([
            new FileEntry(recipe[0].Path, recipe[0].CandidateSha256, recipe[0].OriginalSha256, BackendBackup, "0644"),
            new FileEntry(recipe[1].Path, recipe[1].CandidateSha256, recipe[1].OriginalSha256, FrontendBackup, "0644"),
        ]);
    }

    private bool ExistingBackupsFrom(IReadOnlyList<FileEntry> entries)
    {
        var directory = At(StateDirectory);
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            File.GetUnixFileMode(directory) != PrivateDirectory) return false;
        var permitted = new[] { "original-Nodes.pm", "original-pvemanagerlib.js" };
        if (!Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(permitted.OrderBy(name => name, StringComparer.Ordinal)))
            return false;
        return entries.Take(2).All(entry => entry.BackupPath is not null &&
            HashAt(entry.BackupPath) == entry.OriginalSha256 &&
            File.GetUnixFileMode(At(entry.BackupPath)) == PrivateFile);
    }

    private void BackupBoth(Proposal proposal)
    {
        if (Directory.Exists(At(StateDirectory)))
        {
            if (!ExistingBackupsFrom(proposal.State.Files))
                throw new InvalidDataException("retained backups differ");
            return;
        }
        Directory.CreateDirectory(At(StateDirectory), PrivateDirectory);
        File.SetUnixFileMode(At(StateDirectory), PrivateDirectory);
        LinuxDirectorySync.Flush(Path.GetDirectoryName(At(StateDirectory))!);
        for (var i = 0; i < 2; i++)
        {
            var entry = proposal.State.Files[i];
            var original = File.ReadAllBytes(At(entry.Path));
            if (Hash(original) != entry.OriginalSha256)
                throw new InvalidDataException("original changed before backup");
            Publish(entry.BackupPath!, original, PrivateFile, overwrite: false);
        }
        if (!ExistingBackupsFrom(proposal.State.Files))
            throw new InvalidDataException("both original backups must verify before install");
    }

    private Manifest Load(string hostname, string profileFile)
    {
        var path = At(StatePath);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            File.GetUnixFileMode(path) != PrivateFile || new FileInfo(path).Length is < 30 or > 8192)
            throw new InvalidDataException("manifest is linked or has wrong mode or size");
        var raw = File.ReadAllBytes(path);
        var state = JsonSerializer.Deserialize<Manifest>(raw) ?? throw new InvalidDataException("manifest empty");
        if (state.Format != 1 || state.Host != hostname || state.Version != RecipeVersion(profileFile) ||
            state.RecipeSha256 != Hash(File.ReadAllBytes(profileFile)) ||
            state.Files is not { Length: 5 } || !raw.SequenceEqual(Serialize(state)))
            throw new InvalidDataException("manifest differs from reviewed recipe");
        var workerPath = state.Files[3]?.Path;
        if (workerPath is not (Worker or "/opt/system-watch-proxmox/csharp-fb6ee16/SystemWatch.Worker" or
                "/opt/system-watch-proxmox/csharp-bc265f7/SystemWatch.Worker"))
            throw new InvalidDataException("manifest contains an unsupported worker slot");
        var names = new[] { "/usr/share/perl5/PVE/API2/Nodes.pm", "/usr/share/pve-manager/js/pvemanagerlib.js", Ui, workerPath, Unit };
        for (var i = 0; i < 5; i++)
        {
            var entry = state.Files[i];
            if (entry is null || entry.Path != names[i] || !ValidHash(entry.CandidateSha256) ||
                entry.Mode != (i == 3 ? "0700" : "0644") ||
                (i < 2 ? !ValidHash(entry.OriginalSha256) || entry.BackupPath !=
                    (i == 0 ? BackendBackup : FrontendBackup) : entry.OriginalSha256 is not null || entry.BackupPath is not null))
                throw new InvalidDataException("manifest contains unknown file or hash");
        }
        return state;
    }

    private (bool Complete, string? Drift) InspectFiles(Manifest state)
    {
        if (!ExistingBackupsFromWithManifest(state.Files)) return (false, "original backup missing or changed");
        var complete = true;
        for (var i = 0; i < 5; i++)
        {
            var entry = state.Files[i];
            var hash = HashAt(entry.Path);
            if (hash == entry.CandidateSha256)
            {
                var expectedMode = i == 3 ? Executable : PublicFile;
                if (File.GetUnixFileMode(At(entry.Path)) != expectedMode)
                    return (false, "mode differs: " + entry.Path);
            }
            else if (i < 2 && hash == entry.OriginalSha256) complete = false;
            else if (i >= 2 && !Exists(At(entry.Path))) complete = false;
            else return (false, "unknown file: " + entry.Path);
        }
        return (complete, null);
    }

    private bool ExistingBackupsFromWithManifest(IReadOnlyList<FileEntry> entries)
    {
        var directory = At(StateDirectory);
        if (!Directory.Exists(directory) || File.GetUnixFileMode(directory) != PrivateDirectory ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        var names = new[] { "original-Nodes.pm", "original-pvemanagerlib.js", "simple-install.json" };
        if (!Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(names.OrderBy(name => name, StringComparer.Ordinal)))
            return false;
        return entries.Take(2).All(entry => entry.BackupPath is not null &&
            HashAt(entry.BackupPath) == entry.OriginalSha256 &&
            File.GetUnixFileMode(At(entry.BackupPath)) == PrivateFile);
    }

    private void Publish(string path, byte[] bytes, UnixFileMode mode, bool overwrite,
        string? expectedOldHash = null)
    {
        var target = At(path);
        if (!Path.IsPathFullyQualified(target) || bytes.Length == 0)
            throw new InvalidDataException("invalid install target");
        var parent = Path.GetDirectoryName(target)!;
        if (path == Worker) Directory.CreateDirectory(parent);
        if (!Directory.Exists(parent) || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("install parent missing or linked: " + parent);
        if (overwrite ? HashAt(path) != expectedOldHash : Exists(target))
            throw new InvalidDataException("install target changed or already exists: " + path);
        var stage = Path.Combine(parent, ".system-watch-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = new FileStream(stage, new FileStreamOptions {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Options = FileOptions.WriteThrough, UnixCreateMode = mode,
            }))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.SetUnixFileMode(stage, mode);
            if (Hash(File.ReadAllBytes(stage)) != Hash(bytes) ||
                (overwrite ? HashAt(path) != expectedOldHash : Exists(target)))
                throw new InvalidDataException("target or candidate changed before rename: " + path);
            File.Move(stage, target, overwrite);
            LinuxDirectorySync.Flush(parent);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private void DeleteKnown(string path, string hash)
    {
        if (HashAt(path) != hash) throw new InvalidDataException("target changed before deletion: " + path);
        File.Delete(At(path));
        LinuxDirectorySync.Flush(Path.GetDirectoryName(At(path))!);
    }

    private async Task VerifyLiveAsync(string host, CancellationToken token, string? previousInstance = null)
    {
        var service = await Command("/usr/bin/systemctl", ["is-active", Service], token);
        if (service.Stdout.Trim() != "active") throw new InvalidDataException("worker not active");
        var snapshot = At("/run/system-watch-proxmox/snapshot.json");
        JsonDocument? state = null;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(snapshot))
            {
                state = JsonDocument.Parse(File.ReadAllBytes(snapshot));
                var data = state.RootElement;
                var at = data.GetProperty("snapshot").GetProperty("generated_at").GetDateTimeOffset();
                if (data.GetProperty("schema_version").GetString() == "1.0.0-draft.2" &&
                    data.GetProperty("snapshot").GetProperty("hostname").GetString() == host &&
                    (DateTimeOffset.UtcNow - at).TotalSeconds is >= -5 and <= 10 &&
                    (previousInstance is null || data.GetProperty("snapshot").GetProperty("instance_id").GetString() != previousInstance)) break;
                state.Dispose(); state = null;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        if (state is null) throw new InvalidDataException("worker did not publish a fresh valid snapshot");
        state.Dispose();
        var api = await Command("/usr/bin/pvesh", ["get", $"/nodes/{host}/system-watch", "--output-format", "json"], token);
        using (var response = JsonDocument.Parse(api.Stdout))
            if (response.RootElement.GetProperty("schema_version").GetString() != "1.0.0-draft.2")
                throw new InvalidDataException("authenticated API returned an unexpected schema");
        var unauthorized = await Command("/usr/bin/curl",
            ["-sk", "-o", "/dev/null", "-w", "%{http_code}", $"https://127.0.0.1:8006/api2/json/nodes/{host}/system-watch"], token);
        if (unauthorized.Stdout.Trim() != "401")
            throw new InvalidDataException("unauthorized API request did not return 401");
        Console.WriteLine("Worker and authenticated API verified; check the Summary panel in a browser.");
    }

    private Task<CommandResult> Command(string exe, string[] args, CancellationToken token,
        bool acceptNonZero = false) => _runner.RunAsync(exe, args, TimeSpan.FromSeconds(15), token, acceptNonZero);

    private static string RecipeVersion(string profileFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(profileFile));
        return document.RootElement.GetProperty("pveManagerVersion").GetString()
            ?? throw new InvalidDataException("recipe lacks version");
    }

    private void RequireHost(string host, string versionOutput, string profileFile)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(root) ||
            (host != Environment.MachineName.Split('.')[0] && root == "/") ||
            host.Length is < 2 or > 63 || host[0] is not (>= 'a' and <= 'z') ||
            !host.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            throw new InvalidDataException("requires an explicitly named Linux host");
        if (!versionOutput.Split('\n').Any(line => line.StartsWith("pve-manager: " +
                RecipeVersion(profileFile) + " ", StringComparison.Ordinal)))
            throw new InvalidDataException("host version does not match recipe");
    }

    private string At(string path) => Path.Combine(root, path.TrimStart('/'));
    private string? HashAt(string path)
    {
        var file = At(path);
        if (!Exists(file) || (File.GetAttributes(file) &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static byte[] Serialize(Manifest manifest) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOptions) + "\n");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool ValidHash(string? value) => value is { Length: 64 } &&
        value.All(ch => ch is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
