using System.Text.Json;
using SystemWatch.Collector;
using SystemWatch.Management;

// Reapply recorded hooks after a vendor upgrade, preserving the worker and GUI.
if (OperatingSystem.IsLinux() &&
    ((args.Length == 8 && args[0] == "plan" && args[1] == "repair" &&
        args[2] == "--profile" && args[4] == "--previous-profile" && args[6] == "--expected-host") ||
     (args.Length == 7 && args[0] == "repair" && args[1] == "--profile" &&
        args[3] == "--previous-profile" && args[5] == "--expected-host")))
{
    try
    {
        var plan = args[0] == "plan";
        var offset = plan ? 1 : 0;
        var version = await new ProcessCommandRunner(8192).RunAsync(
            "/usr/bin/pveversion", ["-v"], TimeSpan.FromSeconds(5));
        var installer = new VmRecipeInstaller();
        if (plan)
        {
            var change = await installer.PlanRepairAsync(args[^1], version.Stdout, args[2 + offset], args[4 + offset]);
            Console.WriteLine($"Repair {change.Current.Host}: {change.Current.Version} -> {change.Updated.Version}");
            foreach (var patch in change.Patches)
                Console.WriteLine($"{patch.Path}: new original {patch.OriginalSha256} -> {patch.CandidateSha256}");
            Console.WriteLine("Retain previous evidence; refresh both original backups and manifest; reapply vendor hooks.");
            Console.WriteLine("Preserve worker, UI asset and service unit; restart pvedaemon and pveproxy; verify API.");
            Console.WriteLine("Read-only repair plan; no file or service changed.");
        }
        else
        {
            await installer.RepairAsync(args[^1], version.Stdout, args[2], args[4]);
            Console.WriteLine("Hooks repaired and verified. Hard-refresh the browser.");
        }
        return 0;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or JsonException)
    {
        Console.Error.WriteLine($"Repair refused: {error.Message}");
        return 1;
    }
}

// Update the worker and its manifest together; restart only the project service.
if (OperatingSystem.IsLinux() &&
    ((args.Length == 10 && args[0] == "plan" && args[1] == "worker-update" &&
        args[2] == "--profile" && args[4] == "--asset" && args[6] == "--sha256" && args[8] == "--expected-host") ||
     (args.Length == 9 && args[0] == "worker-update" && args[1] == "--profile" &&
        args[3] == "--asset" && args[5] == "--sha256" && args[7] == "--expected-host")))
{
    try
    {
        var plan = args[0] == "plan";
        var offset = plan ? 1 : 0;
        var version = await new ProcessCommandRunner(8192).RunAsync(
            "/usr/bin/pveversion", ["-v"], TimeSpan.FromSeconds(5));
        var installer = new VmRecipeInstaller();
        var change = installer.PlanWorkerUpdate(args[8 + offset], version.Stdout,
            args[2 + offset], args[4 + offset], args[6 + offset]);
        Console.WriteLine($"Worker: {change.Current.Files[3].Path}");
        Console.WriteLine($"SHA256: {change.Current.Files[3].CandidateSha256} -> {change.Updated.Files[3].CandidateSha256}");
        Console.WriteLine("Restart only system-watch-proxmox; verify a new worker instance and authenticated API.");
        if (plan) Console.WriteLine("Read-only worker update plan; no file or service changed.");
        else
        {
            await installer.UpdateWorkerAsync(args[8], version.Stdout, args[2], args[4], args[6]);
            Console.WriteLine("Worker and private manifest updated and verified.");
        }
        return 0;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or JsonException)
    {
        Console.Error.WriteLine($"Worker update refused: {error.Message}");
        return 1;
    }
}

// Update only the project-owned UI asset; installed vendor files and services stay untouched.
if (OperatingSystem.IsLinux() &&
    ((args.Length == 10 && args[0] == "plan" && args[1] == "ui-update" &&
        args[2] == "--profile" && args[4] == "--asset" && args[6] == "--sha256" &&
        args[8] == "--expected-host") ||
     (args.Length == 9 && args[0] == "ui-update" && args[1] == "--profile" &&
        args[3] == "--asset" && args[5] == "--sha256" && args[7] == "--expected-host")))
{
    try
    {
        var plan = args[0] == "plan";
        var offset = plan ? 1 : 0;
        var version = await new ProcessCommandRunner(8192).RunAsync(
            "/usr/bin/pveversion", ["-v"], TimeSpan.FromSeconds(5));
        var installer = new VmRecipeInstaller();
        var change = installer.PlanUiUpdate(args[8 + offset], version.Stdout,
            args[2 + offset], args[4 + offset], args[6 + offset]);
        Console.WriteLine($"UI asset: {change.Current.Files[2].Path}");
        Console.WriteLine($"SHA256: {change.Current.Files[2].CandidateSha256} -> {change.Updated.Files[2].CandidateSha256}");
        Console.WriteLine("Proxmox files, service unit and worker remain unchanged; no service restart.");
        if (plan)
            Console.WriteLine("Read-only UI update plan; no file or service changed.");
        else
        {
            installer.UpdateUi(args[8], version.Stdout, args[2], args[4], args[6]);
            Console.WriteLine("UI asset and private manifest updated. Hard-refresh the browser to load it.");
        }
        return 0;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or JsonException)
    {
        Console.Error.WriteLine($"UI update refused: {error.Message}");
        return 1;
    }
}

// Clean-host installer driven by reviewed vendor patch recipes.
if (OperatingSystem.IsLinux() &&
    ((args.Length == 8 && args[0] == "plan" && args[1] == "install" &&
        args[2] == "--profile" && args[4] == "--directory" && args[6] == "--expected-host") ||
     (args.Length == 7 && args[0] == "install" && args[1] == "--profile" &&
        args[3] == "--directory" && args[5] == "--expected-host") ||
     (args.Length == 5 && (args[0] is "status" or "remove") &&
        args[1] == "--profile" && args[3] == "--expected-host")))
{
    try
    {
        var planOnly = args[0] == "plan";
        var profileFile = args[planOnly ? 3 : 2];
        var host = args[^1];
        var version = await new ProcessCommandRunner(8192).RunAsync(
            "/usr/bin/pveversion", ["-v"], TimeSpan.FromSeconds(5));
        var installer = new VmRecipeInstaller();
        if (planOnly || args[0] == "install")
        {
            var assets = args[planOnly ? 5 : 4];
            if (planOnly)
            {
                var proposal = await installer.PlanAsync(host, version.Stdout, profileFile, assets);
                Console.WriteLine($"Review plan for {proposal.State.Host} pve-manager {proposal.State.Version}:");
                foreach (var file in proposal.State.Files)
                    Console.WriteLine($"{file.Path}: {file.OriginalSha256 ?? "absent"} -> {file.CandidateSha256}; mode {file.Mode}; backup {file.BackupPath ?? "none"}");
                Console.WriteLine("Both original Proxmox files are backed up before any target changes. One private manifest records these hashes.");
                Console.WriteLine("Then publish worker, UI, unit, backend and frontend; reload systemd, start worker, restart pvedaemon and pveproxy, verify snapshot and API.");
                Console.WriteLine("remove restores verified originals and retains backups. No file or service changed by plan.");
            }
            else
            {
                await installer.InstallAsync(host, version.Stdout, profileFile, assets);
                Console.WriteLine("Installed and verified. Inspect the Summary panel in a browser.");
            }
        }
        else if (args[0] == "remove")
        {
            await installer.RemoveAsync(host, version.Stdout, profileFile);
            Console.WriteLine("Recognized installation removed; original backups retained.");
        }
        else
        {
            var status = installer.Status(host, version.Stdout, profileFile);
            Console.WriteLine("Simple installation: " + status);
            if (status.StartsWith("drift", StringComparison.Ordinal)) return 1;
        }
        return 0;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or JsonException)
    {
        Console.Error.WriteLine($"simple recipe installer refused: {error.Message}");
        return 1;
    }
}
if (args.Length == 5 && args[0] == "review-recipe" && args[1] == "--file" &&
    args[3] == "--expected-host")
{
    try
    {
        var host = Environment.MachineName.Split('.')[0];
        if (host != args[4])
            throw new InvalidDataException("review requires the explicitly named host");
        using var profile = JsonDocument.Parse(File.ReadAllBytes(args[2]));
        var recipeVersion = profile.RootElement.GetProperty("pveManagerVersion").GetString();
        if (recipeVersion is null || !VmPatchRecipe.ValidVersion(recipeVersion))
            throw new InvalidDataException("recipe version is unsupported");
        var version = await new ProcessCommandRunner(8192).RunAsync(
            "/usr/bin/pveversion", ["-v"], TimeSpan.FromSeconds(5));
        if (!version.Stdout.Split('\n').Any(line =>
                line.StartsWith("pve-manager: " + recipeVersion + " ", StringComparison.Ordinal)))
            throw new InvalidDataException("host Proxmox version differs from recipe");
        var rendered = VmPatchRecipe.Render(args[2], File.ReadAllBytes, reviewDraft: true);
        await VmPerlCandidateValidator.ValidateAsync(rendered[0].Bytes);
        foreach (var file in rendered)
            Console.WriteLine($"{file.Path}: original {file.OriginalSha256}, candidate {file.CandidateSha256}");
        Console.WriteLine("Read-only recipe review; no file or service was changed. Draft output is not approved for installation.");
        return 0;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or JsonException)
    {
        Console.Error.WriteLine($"recipe review rejected: {error.Message}");
        return 1;
    }
}
Console.Error.WriteLine("usage: SystemWatch.Management plan install|install --profile FILE --directory ASSETS --expected-host HOST");
Console.Error.WriteLine("       SystemWatch.Management plan repair|repair --profile NEW_FILE --previous-profile INSTALLED_FILE --expected-host HOST");
Console.Error.WriteLine("       SystemWatch.Management status|remove --profile FILE --expected-host HOST");
Console.Error.WriteLine("       SystemWatch.Management plan ui-update|ui-update --profile FILE --asset FILE --sha256 HASH --expected-host HOST");
Console.Error.WriteLine("       SystemWatch.Management plan worker-update|worker-update --profile FILE --asset FILE --sha256 HASH --expected-host HOST");
Console.Error.WriteLine("       SystemWatch.Management review-recipe --file FILE --expected-host HOST");
return 2;
