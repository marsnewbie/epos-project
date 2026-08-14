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
            var services = AppServices.Start();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
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
