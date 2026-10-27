using Pennyworth.Platform.Linux;

// Everything of substance is in HelperApp; this only binds it to the process's streams and its time limit.
using var timeLimit = new CancellationTokenSource(HelperProtocol.MaxRuntime);

// Reading stdin blocks and cannot be cancelled, so a caller that never closes it would outlive the limit: this stops the process.
using var watchdog = new Timer(
    static _ =>
    {
        Console.Error.WriteLine("pennyworth-helper: stopped at the time limit");
        Environment.Exit(HelperExit.TimedOut);
    },
    null,
    HelperProtocol.MaxRuntime + TimeSpan.FromSeconds(10),
    Timeout.InfiniteTimeSpan);

return HelperApp.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError(), timeLimit.Token);
