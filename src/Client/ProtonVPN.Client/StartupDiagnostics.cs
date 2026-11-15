using System.Runtime.ExceptionServices;

namespace ProtonVPN.Client;

/// <summary>Opt-in early startup diagnostics, independent of WinUI and the application logger.</summary>
internal static class StartupDiagnostics
{
    private static string? _path;
    private static int _writing;
    private static int _exceptionCount;

    internal static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("PROTONVPN_STARTUP_DIAGNOSTICS") != "1")
        {
            return;
        }

        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProtonVPN", "StartupDiagnostics");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, $"startup-{Environment.ProcessId}.log");
            Stage("Diagnostic hooks installed");
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Record("Unhandled exception", args.ExceptionObject as Exception);
        }
        catch
        {
            _path = null;
        }
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
    {
        // Bound diagnostic output and avoid recursively tracing a failed log write.
        if (Volatile.Read(ref _writing) == 0 &&
            Interlocked.Increment(ref _exceptionCount) <= 200)
        {
            Record("First-chance exception", args.Exception);
        }
    }

    internal static void Stage(string stage) => Record(stage, null);

    internal static void Record(string stage, Exception? exception)
    {
        if (_path is null || Interlocked.Exchange(ref _writing, 1) != 0)
        {
            return;
        }

        try
        {
            File.AppendAllText(_path,
                $"{DateTimeOffset.UtcNow:O} [{stage}]{Environment.NewLine}" +
                (exception is null ? string.Empty : exception + Environment.NewLine));
        }
        catch
        {
            // Diagnostics must not change startup or exception handling.
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }
}
