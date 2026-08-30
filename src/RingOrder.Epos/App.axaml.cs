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

                Start(desktop, services);

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

    /// <summary>
    /// A machine that has never been told which shop it is asks, once, before
    /// anything else — the same first boot every card terminal and every other
    /// till on the market has.
    /// <para>
    /// Asked once and then never again: a merchant who skipped is trading, and a
    /// prompt every morning teaches them to dismiss it. The shop showing no
    /// tills on our own estate page is the better reminder, because it reaches
    /// the person who can act on it.
    /// </para>
    /// </summary>
    private static void Start(IClassicDesktopStyleApplicationLifetime desktop, AppServices services)
    {
        var neverConnected = services.Entitlement.Current.Source == EntitlementSource.Bundle;

        if (!neverConnected || services.EntitlementStore.SetupOffered())
        {
            Open(desktop, services);
            return;
        }

        var setup = new SetupViewModel(services);
        var window = new SetupWindow { DataContext = setup };

        setup.Finished += () =>
        {
            services.EntitlementStore.RecordSetupOffered();

            // Opened before the setup window closes, and in this order: with
            // ShutdownMode.OnMainWindowClose, closing the only window would take
            // the application down with it.
            Open(desktop, services);
            window.Close();
        };

        desktop.MainWindow = window;
    }

    /// <summary>
    /// The entitlement, not the bundle: a shop that has bought the full till
    /// gets it without waiting for a new bundle, and one that has never reached
    /// the cloud keeps the edition it was shipped with. Resolved from disk, so
    /// nothing waits on a network call to decide which window to open.
    /// </summary>
    private static void Open(IClassicDesktopStyleApplicationLifetime desktop, AppServices services)
    {
        if (services.Entitlement.Current.IsPrintOnly)
            StartPrintOnly(desktop, services);
        else
            StartTill(desktop, services);
    }

    private static void StartTill(IClassicDesktopStyleApplicationLifetime desktop, AppServices services)
    {
        var window = new MainWindow { DataContext = new MainViewModel(services) };
        desktop.MainWindow = window;
        window.Show();
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
