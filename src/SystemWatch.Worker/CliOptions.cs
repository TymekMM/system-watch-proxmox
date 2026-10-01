namespace SystemWatch.Worker;

public sealed record CliOptions(bool Watch, string Output, bool AllowLiveOutput)
{
    public static CliOptions Parse(string[] args, string? hostname = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length is not (3 or 4 or 6) || args[0] is not ("--once" or "--watch") ||
            args[1] != "--output" || !Path.IsPathFullyQualified(args[2]) ||
            string.IsNullOrWhiteSpace(Path.GetFileName(args[2])) ||
            (args.Length >= 4 && args[3] != "--allow-live-output") ||
            (args.Length == 6 && (args[4] != "--expected-host" || !ValidShortHostname(args[5]))))
            throw new ArgumentException("usage: --once|--watch --output ABSOLUTE_PATH [--allow-live-output [--expected-host SHORT_HOSTNAME]]");
        var target = Path.GetFullPath(args[2]);
        var live = Path.GetFullPath("/run/system-watch-proxmox");
        var liveSnapshot = Path.Combine(live, "snapshot.json");
        var directory = Path.GetDirectoryName(target)!;
        var insideLive = directory == live || directory.StartsWith(
            live + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        var allowLive = args.Length >= 4;
        if (allowLive)
        {
            var host = (hostname ?? Environment.MachineName).Split('.')[0];
            var expected = args.Length == 6 ? args[5] : null;
            if (args[0] != "--watch" || target != liveSnapshot || host != expected)
                throw new ArgumentException(
                    "live output requires --watch, the exact snapshot path and the reviewed host");
        }
        else if (insideLive)
            throw new ArgumentException("Offline output must be outside the live runtime directory");
        return new CliOptions(args[0] == "--watch", target, allowLive);
    }

    private static bool ValidShortHostname(string name) =>
        name.Length is >= 2 and <= 63 && name[0] is >= 'a' and <= 'z' &&
        (name[^1] is >= 'a' and <= 'z' or >= '0' and <= '9') &&
        name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
