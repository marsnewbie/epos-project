using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainViewModel(services),
                };

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
}
