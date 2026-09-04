using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SoraV2BatteryTip;

internal static class Program
{
    private const string MutexName = "Global\\SoraV2BatteryTip.Minimal";
    private static IAppEventLog? _log;
    private static int _fatalObserved;

    [STAThread]
    private static void Main()
    {
        var paths = new AppPaths();
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            WriteSingleInstanceRejected(paths.LogsDirectory);
            return;
        }

        using var log = new FlightRecorder(paths.LogsDirectory, paths.LogIdentityKeyPath);
        _log = log;
        RegisterExceptionHandlers();
        log.Write("info", "app.start", nameof(Program), "success", new
        {
            process_id = Environment.ProcessId,
            os_version = Environment.OSVersion.VersionString,
            runtime_version = Environment.Version.ToString(),
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            command_line_argument_count = Environment.GetCommandLineArgs().Length
        });

        try
        {
            paths.Ensure();
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            log.Write("info", "ui_loop.started", nameof(Program), "success");
            Application.Run(new TrayAppContext(paths, log));
            if (Volatile.Read(ref _fatalObserved) == 0)
            {
                log.Write("info", "ui_loop.stopped", nameof(Program), "success");
                log.Write("info", "app.stop", nameof(Program), "success");
                log.Write("info", "app.clean_shutdown", nameof(Program), "success");
            }
            else
            {
                log.Write("error", "ui_loop.stopped", nameof(Program), "failure");
                log.Write("error", "app.stop", nameof(Program), "failure", new { reason = "fatal_observed" });
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _fatalObserved, 1);
            log.CrashSnapshot(ex, nameof(Program), "app.main_failed");
            log.Write("error", "app.stop", nameof(Program), "failure", new { reason = "main_exception" });
            Environment.ExitCode = 1;
        }

        log.Flush(TimeSpan.FromSeconds(2));
        _log = null;
    }

    private static void RegisterExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) =>
        {
            Interlocked.Exchange(ref _fatalObserved, 1);
            _log?.CrashSnapshot(args.Exception, nameof(Application), "app.ui_thread_exception");
            try { Application.Exit(); }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Interlocked.Exchange(ref _fatalObserved, 1);
            var exception = args.ExceptionObject as Exception
                ?? new InvalidOperationException("Non-Exception object reached UnhandledException.");
            _log?.CrashSnapshot(exception, nameof(AppDomain), "app.background_unhandled_exception");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Write("error", "app.unobserved_task_exception", nameof(TaskScheduler), "failure", exception: args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { _log?.Flush(TimeSpan.FromMilliseconds(800)); }
            catch { }
        };
    }

    private static void WriteSingleInstanceRejected(string logsDirectory)
    {
        try
        {
            Directory.CreateDirectory(logsDirectory);
            var now = DateTimeOffset.UtcNow;
            var record = new
            {
                schema = "sora.flight.v1",
                ts_utc = now.ToString("O", CultureInfo.InvariantCulture),
                ts_local = now.ToLocalTime().ToString("O", CultureInfo.InvariantCulture),
                mono_ms = 0,
                seq = 1,
                level = "info",
                @event = "app.single_instance_rejected",
                run_id = Guid.NewGuid(),
                op_id = (string?)null,
                component = nameof(Program),
                thread_id = Environment.CurrentManagedThreadId,
                app_version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                outcome = "skipped",
                device = (object?)null,
                data = new { process_id = Environment.ProcessId },
                error = (object?)null
            };
            var path = Path.Combine(logsDirectory, $"flight-rejected-{now:yyyyMMddTHHmmssfffZ}-{Environment.ProcessId}.jsonl");
            File.WriteAllText(path, JsonSerializer.Serialize(record) + "\n", new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // A rejected second instance must remain silent even when its log cannot be written.
        }
    }
}
