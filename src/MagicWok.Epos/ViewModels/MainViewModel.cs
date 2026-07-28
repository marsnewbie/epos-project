using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagicWok.Epos.Services;

namespace MagicWok.Epos.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly DispatcherTimer _clock;

    public MainViewModel() : this(AppServices.Instance)
    {
    }

    public MainViewModel(AppServices app)
    {
        _app = app;
        Sell = new SellViewModel(app, SetStatus);
        Orders = new OrdersViewModel(app, SetStatus);
        Online = new OnlineViewModel(app, SetStatus);
        Customers = new CustomersViewModel(app, SetStatus);
        SettingsVm = new SettingsViewModel(app, SetStatus);

        ShopName = app.GetSettings().ShopName;
        CurrentPage = Sell;
        NavKey = "sell";
        RefreshNavLabels();
        StatusText = $"Ready · {app.Menu.CountItems()} dishes · {app.GetSettings().KitchenPrinterName}";

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText = DateTime.Now.ToString("HH:mm:ss  ddd d MMM");
        _clock.Start();
        ClockText = DateTime.Now.ToString("HH:mm:ss  ddd d MMM");

        app.CallerId.CallReceived += (_, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Sell.ApplyCallerId(e.PhoneNumber);
                StatusText = $"Caller ID: {e.PhoneNumber}";
                GoSell();
            });
        };

        app.OnlinePoller.OrderReceived += async (_, order) =>
        {
            try
            {
                await app.Print.HandleOnlineOrderAsync(order);
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = $"ONLINE {order.OrderNumber} · kitchen printed";
                    OnlineBadge = "●";
                    Online.Refresh();
                    Orders.Refresh();
                    try
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            Console.Beep(880, 180);
                            Console.Beep(1175, 220);
                        }
                    }
                    catch { /* ignore */ }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                    StatusText = $"Online order error: {ex.Message}");
            }
        };

        _ = BootstrapOnlineAsync();
    }

    public SellViewModel Sell { get; }
    public OrdersViewModel Orders { get; }
    public OnlineViewModel Online { get; }
    public CustomersViewModel Customers { get; }
    public SettingsViewModel SettingsVm { get; }

    [ObservableProperty] private ViewModelBase _currentPage = null!;
    [ObservableProperty] private string _sectionTitle = "Sell";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _clockText = "";
    [ObservableProperty] private string _shopName = "Magic Wok";
    [ObservableProperty] private string _navKey = "sell";
    [ObservableProperty] private string _onlineBadge = "";
    [ObservableProperty] private string _navSell = "SELL\n点单";
    [ObservableProperty] private string _navOrders = "ORDERS\n订单";
    [ObservableProperty] private string _navOnline = "ONLINE\n线上";
    [ObservableProperty] private string _navCustomers = "CUSTOMERS\n顾客";
    [ObservableProperty] private string _navSettings = "SETTINGS\n设置";
    [ObservableProperty] private bool _isSellNav = true;
    [ObservableProperty] private bool _isOrdersNav;
    [ObservableProperty] private bool _isOnlineNav;
    [ObservableProperty] private bool _isCustomersNav;
    [ObservableProperty] private bool _isSettingsNav;

    [RelayCommand]
    private void GoSell() => Navigate("sell", Sell, IsZh ? "点单" : "Sell", () => Sell.RefreshMenu());

    [RelayCommand]
    private void GoOrders() => Navigate("orders", Orders, IsZh ? "订单" : "Orders", () => Orders.Refresh());

    [RelayCommand]
    private void GoOnline() => Navigate("online", Online, IsZh ? "线上单" : "Online", () =>
    {
        OnlineBadge = "";
        Online.Refresh();
    });

    [RelayCommand]
    private void GoCustomers() => Navigate("customers", Customers, IsZh ? "顾客" : "Customers", () => Customers.Refresh());

    [RelayCommand]
    private void GoSettings() => Navigate("settings", SettingsVm, IsZh ? "设置" : "Settings", () =>
    {
        SettingsVm.Reload();
        ShopName = _app.GetSettings().ShopName;
    });

    private void Navigate(string key, ViewModelBase page, string title, Action? onEnter = null)
    {
        NavKey = key;
        IsSellNav = key == "sell";
        IsOrdersNav = key == "orders";
        IsOnlineNav = key == "online";
        IsCustomersNav = key == "customers";
        IsSettingsNav = key == "settings";
        SectionTitle = title;
        CurrentPage = page;
        onEnter?.Invoke();
    }

    [RelayCommand]
    private async Task TestPrintAsync()
    {
        try
        {
            await _app.KitchenPrinter.PrintTestPageAsync();
            StatusText = $"Test print → {_app.KitchenPrinter.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"Test print failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenDrawerAsync()
    {
        try
        {
            await _app.CashDrawer.OpenAsync();
            StatusText = "Cash drawer opened";
        }
        catch (Exception ex)
        {
            StatusText = $"Open drawer failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        var s = _app.GetSettings();
        s.UiLanguage = s.UiLanguage == "zh" ? "en" : "zh";
        _app.SaveSettings(s);
        RefreshNavLabels();
        StatusText = s.UiLanguage == "zh" ? "界面：中文" : "UI language: English";
        if (IsSellNav) GoSell();
        else if (IsOrdersNav) GoOrders();
        else if (IsOnlineNav) GoOnline();
        else if (IsCustomersNav) GoCustomers();
        else GoSettings();
    }

    private void RefreshNavLabels()
    {
        if (IsZh)
        {
            NavSell = "点单\nSELL";
            NavOrders = "订单\nORDERS";
            NavOnline = "线上\nONLINE";
            NavCustomers = "顾客\nCUST";
            NavSettings = "设置\nSET";
        }
        else
        {
            NavSell = "SELL\n点单";
            NavOrders = "ORDERS\n订单";
            NavOnline = "ONLINE\n线上";
            NavCustomers = "CUSTOMERS\n顾客";
            NavSettings = "SETTINGS\n设置";
        }
    }

    private bool IsZh => _app.GetSettings().UiLanguage == "zh";

    public void SetStatus(string text) => StatusText = text;

    private async Task BootstrapOnlineAsync()
    {
        if (!_app.GetSettings().OnlinePollingEnabled) return;
        try
        {
            await _app.OnlinePoller.StartAsync();
            StatusText += " · Online poller on";
        }
        catch (Exception ex)
        {
            StatusText += $" · Poller: {ex.Message}";
        }
    }
}
