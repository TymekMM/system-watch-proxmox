using SystemWatch.Collector;
using SystemWatch.Contracts;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.AtomicPublication.Smoke -- tests/fixtures/snapshot.example.json");
    return 2;
}
if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("This Unix permission smoke test requires Linux.");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

var root = Path.Combine(Path.GetTempPath(), "system-watch-publish-" + Guid.NewGuid().ToString("N"));
var directory = Path.Combine(root, "private");
var target = Path.Combine(directory, "snapshot.json");
var original = File.ReadAllText(args[0]);
SnapshotDocument Fixture() => SnapshotCodec.Deserialize(original);
try
{
    var publisher = new AtomicSnapshotPublisher(target);
    var first = Fixture();
    await publisher.PublishAsync(first);
    Check(SnapshotCodec.Deserialize(File.ReadAllText(target)).Snapshot.Sequence == 1 &&
          File.ReadAllText(target).EndsWith('\n'),
          "initial snapshot is readable and newline terminated");
    var privateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    var privateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    Check(File.GetUnixFileMode(directory) == privateDir &&
          File.GetUnixFileMode(target) == privateFile,
          "runtime directory is 0700 and snapshot is 0600");

    var second = Fixture();
    second.Snapshot.Sequence = 2;
    await publisher.PublishAsync(second);
    Check(SnapshotCodec.Deserialize(File.ReadAllText(target)).Snapshot.Sequence == 2 &&
          File.GetUnixFileMode(target) == privateFile,
          "replacement publishes new sequence with private permissions");

    var failed = new AtomicSnapshotPublisher(target, temporary =>
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Unix permission check requires Linux");
        Check(File.Exists(temporary) && File.GetUnixFileMode(temporary) == privateFile &&
              SnapshotCodec.Deserialize(File.ReadAllText(temporary)).Snapshot.Sequence == 3,
              "temporary file is complete and private before replacement");
        throw new IOException("injected interruption before rename");
    });
    var third = Fixture();
    third.Snapshot.Sequence = 3;
    try
    {
        await failed.PublishAsync(third);
        throw new Exception("injected failure was ignored");
    }
    catch (IOException error) when (error.Message.Contains("injected interruption"))
    {
        Console.WriteLine("PASS injected interruption stops replacement");
    }
    Check(SnapshotCodec.Deserialize(File.ReadAllText(target)).Snapshot.Sequence == 2 &&
          !Directory.EnumerateFiles(directory, ".snapshot.json.tmp-*").Any(),
          "failed replacement retains previous snapshot and cleans temporary file");

    var cancel = new CancellationTokenSource();
    var canceled = new AtomicSnapshotPublisher(target, _ => cancel.Cancel());
    try
    {
        await canceled.PublishAsync(third, cancel.Token);
        throw new Exception("cancellation was ignored");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("PASS cancellation before rename leaves prior snapshot");
    }
    Check(SnapshotCodec.Deserialize(File.ReadAllText(target)).Snapshot.Sequence == 2 &&
          !Directory.EnumerateFiles(directory, ".snapshot.json.tmp-*").Any(),
          "cancelled publication cleans temporary file");

    var invalid = Fixture();
    invalid.Temperatures[0].SourceId = "missing:source";
    try
    {
        await publisher.PublishAsync(invalid);
        throw new Exception("invalid document was published");
    }
    catch (InvalidDataException error) when (error.Message.Contains("snapshot validation"))
    {
        Console.WriteLine("PASS invalid snapshot rejected before file write");
    }
    Check(SnapshotCodec.Deserialize(File.ReadAllText(target)).Snapshot.Sequence == 2,
          "invalid snapshot leaves previous publication intact");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
