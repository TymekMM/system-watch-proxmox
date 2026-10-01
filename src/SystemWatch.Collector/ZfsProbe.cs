using System.Globalization;

namespace SystemWatch.Collector;

public interface IPathResolver
{
    string Resolve(string path);
}

/// <summary>Resolve by-id symlinks before mapping ZFS leaves to lsblk paths.</summary>
public sealed class PhysicalPathResolver : IPathResolver
{
    public string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var absolute = Path.GetFullPath(path);
        try { return File.ResolveLinkTarget(absolute, returnFinalTarget: true)?.FullName ?? absolute; }
        catch (IOException) { return absolute; } // A missing FAULTED leaf may have no device.
    }
}

/// <summary>Read-only ZFS inventory; callers report failures as unavailable sources.</summary>
public sealed class ZfsProbe(ICommandRunner runner, IPathResolver paths)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public async Task<ZfsInventoryResult> CollectAsync(
        string? observedAt = null, CancellationToken cancellationToken = default)
    {
        var at = observedAt ?? DateTimeOffset.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        var listed = await runner.RunAsync("zpool",
            ["list", "-H", "-p", "-o", "name,size,alloc,free,cap,frag,health"],
            Deadline, cancellationToken);
        var status = await runner.RunAsync("zpool", ["status", "-j", "-p", "-P"],
            Deadline, cancellationToken);
        var blocks = await runner.RunAsync("lsblk",
            ["-J", "-b", "-o", "NAME,PATH,TYPE,WWN,SERIAL,TRAN,ROTA,MODEL,FSTYPE,MOUNTPOINTS"],
            Deadline, cancellationToken);
        var pools = ZpoolStatusParser.Parse(status.Stdout, ZpoolListParser.Parse(listed.Stdout));
        var disks = LsblkInventoryParser.Parse(blocks.Stdout);
        return ZfsInventoryAssembler.Assemble(pools, disks, paths.Resolve, at);
    }
}
