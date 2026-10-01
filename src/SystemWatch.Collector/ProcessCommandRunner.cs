using System.Diagnostics;
using System.Text;

namespace SystemWatch.Collector;

public sealed record CommandResult(string Stdout, string Stderr, int ExitCode);

public sealed class CommandExitException(int exitCode, string stderr)
    : IOException($"command exited with code {exitCode}: {stderr}")
{
    public int ExitCode { get; } = exitCode;
    public string Stderr { get; } = stderr;
}

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        bool acceptNonZeroExit = false);
}

/// <summary>Bounded, shell-free command execution for read-only probes.</summary>
public sealed class ProcessCommandRunner(int maxOutputChars = 1_048_576) : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        bool acceptNonZeroExit = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputChars));
        cancellationToken.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput, "stdout", process, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError, "stderr", process, deadline.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            var result = new CommandResult(await stdout, await stderr, process.ExitCode);
            if (result.ExitCode != 0 && !acceptNonZeroExit)
                throw new CommandExitException(result.ExitCode, result.Stderr);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"command exceeded {timeout.TotalSeconds:g} seconds: {executable}");
        }
        finally
        {
            KillIfRunning(process);
            // A child stuck in uninterruptible I/O cannot hold this caller indefinitely.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }
    }

    private async Task<string> ReadBoundedAsync(StreamReader reader, string stream,
        Process process, CancellationToken token)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return output.ToString();
            if (output.Length + count > maxOutputChars)
            {
                KillIfRunning(process);
                throw new InvalidDataException($"command {stream} exceeded {maxOutputChars} characters");
            }
            output.Append(buffer, 0, count);
        }
    }

    private static void KillIfRunning(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* exited concurrently */ }
    }
}
