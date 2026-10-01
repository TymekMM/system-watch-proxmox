using System.Text;
using SystemWatch.Management;

var path = args.Length == 1 ? args[0] : "integration/proxmox/system-watch-proxmox.service";
var original = File.ReadAllBytes(path);
var before = original.ToArray();
var candidate = Encoding.UTF8.GetString(VmServiceCandidateRenderer.Render(original, "node-other"));
Check("reviewed template remains unchanged", before.SequenceEqual(original));
Check("candidate preserves private runtime and service restrictions",
    candidate.Contains("RuntimeDirectoryMode=0700\nUMask=0077\n", StringComparison.Ordinal) &&
    candidate.Contains("ProtectSystem=strict\nReadWritePaths=/run/system-watch-proxmox\n", StringComparison.Ordinal));
Check("C# command selects one writer and exact host", candidate.Split('\n').Count(line =>
    line.StartsWith("ExecStart=", StringComparison.Ordinal)) == 1 &&
    candidate.Contains("ExecStart=/opt/system-watch-proxmox/worker/SystemWatch.Worker --watch --output /run/system-watch-proxmox/snapshot.json --allow-live-output --expected-host node-other\n", StringComparison.Ordinal) &&
    !candidate.Contains("async_snapshot.py", StringComparison.Ordinal));
Check("bundle extraction stays under private runtime", candidate.Contains(
    "Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/run/system-watch-proxmox/.net\n", StringComparison.Ordinal));
Check("node-production unit binds the explicitly reviewed host",
    Encoding.UTF8.GetString(VmServiceCandidateRenderer.Render(original, "node-production"))
        .Contains("--expected-host node-production\n", StringComparison.Ordinal));
Check("node-fixture unit binds the explicitly reviewed host",
    Encoding.UTF8.GetString(VmServiceCandidateRenderer.Render(original, "node-fixture"))
        .Contains("--expected-host node-fixture\n", StringComparison.Ordinal));
Reject("hostname injection refused", () => VmServiceCandidateRenderer.Render(original, "node-other\nExecStart=/bin/sh"));
var changed = original.ToArray();
changed[^1] = (byte)'X';
Reject("changed unit template refused", () => VmServiceCandidateRenderer.Render(changed, "node-other"));

static void Check(string name, bool passes)
{
    if (!passes) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
}

static void Reject(string name, Action action)
{
    try { action(); }
    catch (InvalidDataException) { Console.WriteLine("PASS " + name); return; }
    throw new Exception("FAIL " + name);
}
