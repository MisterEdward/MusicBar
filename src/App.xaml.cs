using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using TaskbarMusic.Interop;

namespace TaskbarMusic;

public partial class App : Application
{
    private MainWindow? _main;
    private CatWindow? _cat;
    private ThemeWatcher? _themeWatcher;

    // ---- Crash logging ------------------------------------------------
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarMusic");
    private static readonly string LogPath = Path.Combine(LogDir, "crash.log");
    private static readonly object LogLock = new();
    private const long MaxLogBytes = 2 * 1024 * 1024; // 2 MB, then trim to the tail

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // App-wide crash safety net.
        // 1) Anything that escapes a UI-thread callback lands here — log the
        //    full chain, mark handled so one bad frame can't kill the pill.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // 2) Anything thrown on a non-Dispatcher thread (WASAPI capture, WinRT
        //    thread-pool, finalizers). Logging-only: the CLR is already tearing
        //    down by the time this fires — the source-level try/catch wraps are
        //    the real fix; this just captures the stack.
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        // 3) A faulted fire-and-forget Task nobody observed.
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // The idle-cat overlay is a separate click-through window so it can
        // slide in from a screen edge without ever blocking clicks.
        _cat = new CatWindow();
        _main = new MainWindow(_cat);

        _themeWatcher = new ThemeWatcher(Dispatcher);
        _themeWatcher.ThemeChanged += OnThemeChanged;

        _main.Show();
        _cat.Show();
    }

    private void OnThemeChanged(bool light)
    {
        if (_main is not null)
            ThemeWatcher.ApplyResources(_main.Resources, light);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _themeWatcher?.Dispose();
        _themeWatcher = null;

        _cat?.StopIdle();

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("UI thread (DispatcherUnhandledException)", e.Exception);
        e.Handled = true; // survive; the stack is already captured
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogException($"Non-UI thread (AppDomain.UnhandledException, IsTerminating={e.IsTerminating})",
            e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("Unobserved Task exception", e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// Shared crash logger. Internal so background-thread call sites (WASAPI
    /// capture, WinRT callbacks, the low-level hook) can log into the same file
    /// from their own try/catch — those threads can't route through the
    /// Dispatcher handler.
    /// </summary>
    internal static void LogException(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);

            var sb = new StringBuilder();
            sb.Append("==== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append("  ").Append(source).AppendLine(" ====");
            sb.Append("OS: ").Append(Environment.OSVersion)
              .Append("  CLR: ").Append(Environment.Version)
              .Append("  64-bit: ").AppendLine(Environment.Is64BitProcess.ToString());

            if (ex is null)
            {
                sb.AppendLine("(no Exception object was supplied)");
            }
            else
            {
                int depth = 0;
                for (Exception? cur = ex; cur is not null; cur = cur.InnerException, depth++)
                {
                    sb.Append(depth == 0 ? "" : new string(' ', depth * 2))
                      .Append(depth == 0 ? "" : "Inner: ")
                      .Append(cur.GetType().FullName).Append(": ").AppendLine(cur.Message);
                    sb.AppendLine(cur.StackTrace);

                    if (cur is AggregateException agg)
                        foreach (var inner in agg.InnerExceptions)
                        {
                            sb.Append("  Aggregate branch: ").Append(inner.GetType().FullName)
                              .Append(": ").AppendLine(inner.Message);
                            sb.AppendLine(inner.StackTrace);
                        }
                }
            }
            sb.AppendLine();

            lock (LogLock)
            {
                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
                TrimIfHuge();
            }
        }
        catch
        {
            // Logging must never itself throw while we're handling a crash.
        }
    }

    private static void TrimIfHuge()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > MaxLogBytes)
            {
                var text = File.ReadAllText(LogPath);
                int keepFrom = Math.Max(0, text.Length - (int)(MaxLogBytes / 2));
                File.WriteAllText(LogPath, "...(trimmed)...\n" + text[keepFrom..]);
            }
        }
        catch
        {
            // Best effort; never let log maintenance crash the crash handler.
        }
    }
}
