using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Services;
using RingOrder.Epos.ViewModels;
using RingOrder.Epos.Views;

namespace RingOrder.Epos;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            // Nothing is started unless there is a window to show it in. A
            // headless host — the UI tests — loads this application for its
            // styles and tokens, and must not open the live shop database,
            // start print workers or begin polling a merchant's website.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var services = AppServices.Start();

                // The entitlement, not the bundle: a shop that has bought the
                // full till gets it without waiting for a new bundle, and one
                // that has never reached the cloud keeps the edition it was
                // shipped with. Resolved from disk, so this does not wait on a
                // network call to decide which window to open.
                if (services.Entitlement.Current.IsPrintOnly)
                    StartPrintOnly(desktop, services);
                else
                    StartTill(desktop, services);

                desktop.ShutdownRequested += async (_, _) =>
                {
                    try { await services.OnlinePoller.StopAsync(); } catch { /* ignore */ }
                    services.Db.Dispose();
                };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("EPOS startup failed:");
            Console.Error.WriteLine(ex);
            throw;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void StartTill(IClassicDesktopStyleApplicationLifetime desktop, AppServices services)
    {
        desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(services) };
    }

    /// <summary>
    /// The web-order printer: a tray icon and a window that is not shown.
    /// <para>
    /// This machine sits in a corner and nobody watches it. A full-screen till
    /// would be minimised on the first day and then nobody could tell whether it
    /// was still running — which is the state that loses a shop its orders. It
    /// lives in the tray, keeps printing with the window closed, and closing
    /// that window hides it rather than stopping the machine.
    /// </para>
    /// </summary>
    private static void StartPrintOnly(IClassicDesktopStyleApplicationLifetime desktop, AppServices services)
    {
        // The window exists from the start so the poller behind it is running,
        // but the shop is not shown a window it did not ask for.
        var window = new MonitorWindow { DataContext = new MonitorViewModel(services) };

        // Closing is hiding. Quitting is a deliberate choice from the tray menu,
        // because a shop that closed the window and lost its web orders would
        // have no way of knowing.
        window.Closing += (_, e) =>
        {
            if (desktop.ShutdownMode != ShutdownMode.OnExplicitShutdown) return;
            e.Cancel = true;
            window.Hide();
        };

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.MainWindow = window;

        var open = new NativeMenuItem("Open");
        open.Click += (_, _) => { window.Show(); window.Activate(); };

        var quit = new NativeMenuItem("Quit — stops printing web orders");
        quit.Click += (_, _) => desktop.Shutdown();

        var tray = new TrayIcon
        {
            ToolTipText = $"RingOrder — {services.GetSettings().ShopName}",
            IsVisible = true,
            Menu = [open, quit],
        };
        tray.Clicked += (_, _) => { window.Show(); window.Activate(); };

        TrayIcon.SetIcons(Current!, [tray]);
        AppLog.Info("app", "print-only edition: running in the tray");
    }
}
