using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using RingOrder.Epos;
using RingOrder.Epos.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(RingOrder.Epos.Tests.HeadlessAppBuilder))]

namespace RingOrder.Epos.Tests;

/// <summary>
/// Builds the real application for the tests: the same App, and therefore the
/// same tokens, styles and control themes the merchant sees.
/// <para>
/// It does not start <c>AppServices</c>, because there is no desktop lifetime
/// here — see <c>App.OnFrameworkInitializationCompleted</c>. A UI test that
/// opened the live shop database would be the same defect this project has
/// already had twice.
/// </para>
/// </summary>
public static class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// Every screen is constructed, shown and laid out for real.
/// <para>
/// What this catches: anything that <em>throws</em> while a screen is built or
/// laid out — a template that blows up, a converter that is not there, a control
/// that will not initialise. On four of these screens nobody had ever opened
/// them, so nothing had established even that much.
/// </para>
/// <para>
/// What it does <em>not</em> catch, checked rather than assumed: a missing
/// <c>StaticResource</c>. Avalonia leaves the property at its default and logs,
/// so a view referencing a brush that does not exist still loads and still
/// passes here. Proven by pointing this view at a resource that does not exist
/// and watching the test go green. Wrong colours are caught by looking, not by
/// this.
/// </para>
/// <para>
/// Property and command names are covered separately and better: every view
/// declares <c>x:DataType</c> and the project compiles bindings, so a typo is a
/// build error rather than a silent no-op.
/// </para>
/// </summary>
public class ViewLoadTests
{
    private static void Show(Control view)
    {
        // A window, measured and arranged: enough to force templates to build
        // and every resource on them to resolve.
        var window = new Window { Content = view, Width = 1366, Height = 768 };
        window.Show();
        window.Measure(new Size(1366, 768));
        window.Arrange(new Rect(0, 0, 1366, 768));

        Assert.True(view.IsInitialized);
    }

    [AvaloniaFact] public void The_till_screen_loads() => Show(new TillView());
    [AvaloniaFact] public void The_orders_screen_loads() => Show(new OrdersView());
    [AvaloniaFact] public void The_customers_screen_loads() => Show(new CustomersView());
    [AvaloniaFact] public void The_settings_screen_loads() => Show(new SettingsView());

    /// <summary>The newest screen, and the one nobody has clicked.</summary>
    [AvaloniaFact] public void The_delivery_board_loads() => Show(new DispatchView());

    /// <summary>
    /// The first screen a new installation shows, and the one most likely to be
    /// looked at by somebody who has never seen this software before.
    /// </summary>
    [AvaloniaFact] public void The_setup_screen_loads() => Show(new SetupView());

    /// <summary>The print-only edition's whole interface.</summary>
    [AvaloniaFact]
    public void The_web_order_monitor_loads()
    {
        var window = new MonitorWindow();
        window.Show();
        window.Measure(new Size(720, 620));
        window.Arrange(new Rect(0, 0, 720, 620));

        Assert.True(window.IsInitialized);
    }

    [AvaloniaFact]
    public void The_main_window_loads_with_every_screen_in_it()
    {
        var window = new MainWindow();
        window.Show();
        window.Measure(new Size(1366, 768));
        window.Arrange(new Rect(0, 0, 1366, 768));

        Assert.True(window.IsInitialized);
    }
}
