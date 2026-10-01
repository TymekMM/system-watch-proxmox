using SystemWatch.Collector;

if (args.Length > 0 && args[0] == "child")
{
    switch (args[1])
    {
        case "success":
            Console.Out.Write(args[2]);
            Console.Error.Write("diagnostic");
            return 0;
        case "fail":
            Console.Error.Write("failed with reason");
            return 7;
        case "slow":
            await Task.Delay(TimeSpan.FromSeconds(10));
            return 0;
        case "flood":
            Console.Out.Write(new string('x', 8192));
            Console.Out.Flush();
            return 0;
        case "flood-error":
            Console.Error.Write(new string('x', 8192));
            Console.Error.Flush();
            return 0;
    }
    return 3;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

var executable = Environment.ProcessPath ?? throw new Exception("missing dotnet process path");
var assembly = typeof(Program).Assembly.Location;
string[] Child(string mode, params string[] extra) =>
    Path.GetFileNameWithoutExtension(executable) == "dotnet"
        ? [assembly, "child", mode, ..extra]
        : ["child", mode, ..extra];

try
{
    ICommandRunner runner = new ProcessCommandRunner(maxOutputChars: 1024);
    var success = await runner.RunAsync(executable, Child("success", "one two; $HOME"),
        TimeSpan.FromSeconds(5));
    Check(success.Stdout == "one two; $HOME" && success.Stderr == "diagnostic" &&
          success.ExitCode == 0, "arguments passed literally and both streams captured");

    try
    {
        await runner.RunAsync(executable, Child("fail"), TimeSpan.FromSeconds(5));
        throw new Exception("nonzero exit accepted");
    }
    catch (CommandExitException error)
    {
        Check(error.ExitCode == 7 && error.Stderr == "failed with reason",
              "nonzero exit and stderr reported");
    }

    var inspect = await runner.RunAsync(executable, Child("fail"), TimeSpan.FromSeconds(5),
        acceptNonZeroExit: true);
    Check(inspect.ExitCode == 7 && inspect.Stderr == "failed with reason",
          "explicit opt-in preserves nonzero exit and output");

    try
    {
        await runner.RunAsync(executable, Child("slow"), TimeSpan.FromMilliseconds(300));
        throw new Exception("deadline ignored");
    }
    catch (TimeoutException) { Console.WriteLine("PASS deadline stops slow child"); }

    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    try
    {
        await runner.RunAsync(executable, Child("slow"), TimeSpan.FromSeconds(5), cancellation.Token);
        throw new Exception("caller cancellation ignored");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    { Console.WriteLine("PASS caller cancellation preserved"); }

    try
    {
        await runner.RunAsync(executable, Child("flood"), TimeSpan.FromSeconds(5));
        throw new Exception("output limit ignored");
    }
    catch (InvalidDataException)
    { Console.WriteLine("PASS stdout limit stops noisy child"); }

    try
    {
        await runner.RunAsync(executable, Child("flood-error"), TimeSpan.FromSeconds(5));
        throw new Exception("stderr limit ignored");
    }
    catch (InvalidDataException)
    { Console.WriteLine("PASS stderr limit stops noisy child"); }

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
