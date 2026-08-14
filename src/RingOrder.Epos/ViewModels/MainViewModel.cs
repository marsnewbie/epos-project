using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Services;

namespace RingOrder.Epos.ViewModels;

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
        Sell = new SellViewModel(app, SetStatus, () => GoOrders());
        Orders = new OrdersViewModel(app, SetStatus, order =>
        {
            Sell.LoadOrderForContinue(order);
            GoSell();
        });
        Online = new OnlineViewModel(app, SetStatus);
        Customers = new CustomersViewModel(app, SetStatus, customer =>
        {
            Sell.StartDeliveryForCustomer(customer);
            GoSell();
        });
        SettingsVm = new SettingsViewModel(app, SetStatus, () =>
        {
            Sell.ReloadQuickNotes();
            Sell.RefreshMenu();
            RefreshAllUiLanguage();
            ShopName = _app.GetSettings().ShopName;
        });

        ShopName = app.GetSettings().ShopName;
        CurrentPage = Sell;
        NavKey = "sell";
        RefreshAllUiLanguage();
        StatusText = UiText.Pick(
            $"Ready · {app.Menu.CountItems()} dishes · {app.GetSettings().KitchenPrinterName}",
            $"就绪 · {app.Menu.CountItems()} 道菜 · {app.GetSettings().KitchenPrinterName}");

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText = DateTime.Now.ToString("HH:mm:ss  ddd d MMM");
        _clock.Start();
        ClockText = DateTime.Now.ToString("HH:mm:ss  ddd d MMM");

        app.CallerId.CallReceived += (_, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Sell.ApplyCallerId(e.PhoneNumber);
                StatusText = UiText.Pick($"Caller ID: {e.PhoneNumber}", $"来电: {e.PhoneNumber}");
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
                    StatusText = UiText.Pick(
                        $"ONLINE {order.OrderNumber} · kitchen printed",
                        $"线上 {order.OrderNumber} · 已打厨房");
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
                    StatusText = UiText.Pick($"Online order error: {ex.Message}", $"线上单错误: {ex.Message}"));
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
    [ObservableProperty] private string _shopName = "";
    [ObservableProperty] private string _navKey = "sell";
    [ObservableProperty] private string _onlineBadge = "";
    [ObservableProperty] private string _navSell = "Sell";
    [ObservableProperty] private string _navOrders = "Orders";
    [ObservableProperty] private string _navOnline = "Online";
    [ObservableProperty] private string _navCustomers = "Customers";
    [ObservableProperty] private string _navSettings = "Settings";
    [ObservableProperty] private string _lblLanguage = "中文";
    [ObservableProperty] private string _lblDrawer = "Drawer";
    [ObservableProperty] private string _languageHint = "";
    [ObservableProperty] private bool _isSellNav = true;
    [ObservableProperty] private bool _isOrdersNav;
    [ObservableProperty] private bool _isOnlineNav;
    [ObservableProperty] private bool _isCustomersNav;
    [ObservableProperty] private bool _isSettingsNav;

    [RelayCommand]
    private void GoSell() => Navigate("sell", Sell, UiText.NavSell, () =>
    {
        Sell.RefreshMenu();
        Sell.RefreshUiLabels();
        Sell.RefreshHeldList();
    });

    [RelayCommand]
    private void GoOrders() => Navigate("orders", Orders, UiText.NavOrders, () =>
    {
        Orders.RefreshUiLabels();
        Orders.Refresh();
    });

    [RelayCommand]
    private void GoOnline() => Navigate("online", Online, UiText.NavOnline, () =>
    {
        OnlineBadge = "";
        Online.RefreshUiLabels();
        Online.Refresh();
    });

    [RelayCommand]
    private void GoCustomers() => Navigate("customers", Customers, UiText.NavCustomers, () =>
    {
        Customers.RefreshUiLabels();
        Customers.Refresh();
    });

    [RelayCommand]
    private void GoSettings() => Navigate("settings", SettingsVm, UiText.NavSettings, () =>
    {
        SettingsVm.RefreshUiLabels();
        SettingsVm.Reload();
        ShopName = _app.GetSettings().ShopName;
    });

    private void Navigate(string key, ViewModelBase page, string title, Action? onEnter = null)
    {
        if (NavKey == "sell" && key != "sell")
            Sell.CompletePendingSettlement();

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
    private async Task OpenDrawerAsync()
    {
        if (!await UiPrompt.RequireManagerPinAsync(_app.GetSettings(), UiText.Pick("Open drawer", "开钱箱")))
            return;
        try
        {
            await _app.CashDrawer.OpenAsync();
            StatusText = UiText.Pick("Cash drawer opened", "钱箱已打开");
        }
        catch (Exception ex)
        {
            StatusText = UiText.Pick($"Open drawer failed: {ex.Message}", $"开钱箱失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        var s = _app.GetSettings();
        s.UiLanguage = s.UiLanguage == "zh" ? "en" : "zh";
        _app.SaveSettings(s);
        RefreshAllUiLanguage();
        StatusText = UiText.Pick(
            "UI language: English (menu names unchanged)",
            "界面语言：中文（菜名不变）");
        // Re-enter current page so titles/lists refresh
        if (IsSellNav) GoSell();
        else if (IsOrdersNav) GoOrders();
        else if (IsOnlineNav) GoOnline();
        else if (IsCustomersNav) GoCustomers();
        else GoSettings();
    }

    private void RefreshAllUiLanguage()
    {
        NavSell = UiText.NavSell;
        NavOrders = UiText.NavOrders;
        NavOnline = UiText.NavOnline;
        NavCustomers = UiText.NavCustomers;
        NavSettings = UiText.NavSettings;
        LblLanguage = UiText.LanguageToggle;
        LblDrawer = UiText.Drawer;
        LanguageHint = UiText.UiLangNote;
        Sell.RefreshUiLabels();
        Orders.RefreshUiLabels();
        Online.RefreshUiLabels();
        Customers.RefreshUiLabels();
        SettingsVm.RefreshUiLabels();
    }

    public void SetStatus(string text) => StatusText = text;

    private async Task BootstrapOnlineAsync()
    {
        if (!_app.GetSettings().OnlinePollingEnabled) return;
        try
        {
            await _app.OnlinePoller.StartAsync();
            StatusText += UiText.Pick(" · Online poller on", " · 线上轮询已开");
        }
        catch (Exception ex)
        {
            StatusText += UiText.Pick($" · Poller: {ex.Message}", $" · 轮询: {ex.Message}");
        }
    }
}
