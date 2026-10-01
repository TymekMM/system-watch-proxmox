using System.Text;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

/// <summary>Publish one validated snapshot by replacing a file in its private directory.</summary>
public sealed class AtomicSnapshotPublisher(string outputPath, Action<string>? beforeReplace = null)
{
    private static readonly UnixFileMode DirectoryMode = UnixFileMode.UserRead |
        UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode FileModePrivate = UnixFileMode.UserRead |
        UnixFileMode.UserWrite;

    public async Task PublishAsync(SnapshotDocument document,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("snapshot publication requires Linux");
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(outputPath) || string.IsNullOrWhiteSpace(Path.GetFileName(outputPath)))
            throw new ArgumentException("snapshot output must be an absolute file path", nameof(outputPath));
        var errors = SnapshotSemantics.Validate(document);
        if (errors.Count > 0)
            throw new InvalidDataException("snapshot validation: " + string.Join("; ", errors));
        var bytes = Encoding.UTF8.GetBytes(SnapshotCodec.Serialize(document) + "\n");

        var target = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        if (new DirectoryInfo(directory).LinkTarget is not null)
            throw new IOException("snapshot directory must not be a symlink");
        File.SetUnixFileMode(directory, DirectoryMode);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(target) +
            ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, UnixCreateMode = FileModePrivate,
            }))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            beforeReplace?.Invoke(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
