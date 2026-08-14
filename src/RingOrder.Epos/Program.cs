using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using RingOrder.Epos.Data;
using RingOrder.Epos.Services;

namespace RingOrder.Epos;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't
    // initialized yet and stuff might break.
    /// <summary>
    /// Held for the lifetime of the process. Two tills on one PC would share a
    /// database, double-print and disagree about the shift — and it happens for
    /// the dullest reason there is: someone double-clicks the icon twice.
    /// </summary>
    private static Mutex? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Global\RingOrder.Epos", out var isOnly);
        if (!isOnly)
        {
            AppLog.Warn("app", "another till is already running on this PC; this one will not start");
            return;
        }

        AppLog.Start();

        if (LocalPaths.FallbackReason is { } reason)
            AppLog.Warn("app",
                $"machine-wide data folder unusable ({reason}); using {LocalPaths.RootDirectory}. " +
                "Fix the folder permissions — a per-user copy is invisible to support and to a second Windows account.");

        // A till that dies without a word is the worst possible failure: the
        // shop does not know whether the last order was saved. Everything that
        // escapes is written down before the process goes.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("crash", "unhandled exception", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("crash", "unobserved task exception", e.Exception);
            e.SetObserved();   // a background fault must not take the till with it
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Error("crash", "startup failed", ex);
            throw;
        }
        finally
        {
            AppLog.Info("app", "stopped");
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
