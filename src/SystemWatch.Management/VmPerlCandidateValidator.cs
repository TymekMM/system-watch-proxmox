using System.ComponentModel;
using System.Diagnostics;

namespace SystemWatch.Management;

/// <summary>Checks the rendered Perl module via stdin, without staging a host file.</summary>
public static class VmPerlCandidateValidator
{
    public static async Task ValidateAsync(byte[] candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Length == 0 || candidate.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Perl candidate size outside reviewed bounds");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/perl")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("-");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var started = false;
        try
        {
            if (!process.Start())
                throw new InvalidDataException("Perl syntax check could not start");
            started = true;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(candidate, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            var diagnostic = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidDataException("Perl candidate syntax check failed: " +
                    diagnostic[..Math.Min(diagnostic.Length, 2048)].Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidDataException("Perl candidate syntax check timed out");
        }
        catch (Win32Exception error)
        {
            throw new InvalidDataException("Perl syntax checker is unavailable", error);
        }
        finally
        {
            if (started && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
