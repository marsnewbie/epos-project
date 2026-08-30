using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Hardware;
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
        Till = new TillViewModel(app, SetStatus, () => GoOrders());
        Orders = new OrdersViewModel(app, SetStatus, order =>
        {
            Till.LoadOrderForContinue(order);
            GoTill();
        });
        Dispatch = new DispatchViewModel(app, SetStatus);
        Customers = new CustomersViewModel(app, SetStatus, customer =>
        {
            Till.StartDeliveryForCustomer(customer);
            GoTill();
        });
        SettingsVm = new SettingsViewModel(app, SetStatus, () =>
        {
            Till.ReloadQuickNotes();
            Till.RefreshMenu();
            RefreshAllUiLanguage();
            ShopName = _app.GetSettings().ShopName;
        });

        ShopName = app.GetSettings().ShopName;
        RefreshSession();
        CurrentPage = Till;
        NavKey = "till";
        RefreshAllUiLanguage();
        StatusText = UiText.Pick(
            $"Ready · {app.Menu.CountItems()} dishes · {app.GetSettings().KitchenPrinterName}",
            $"就绪 · {app.Menu.CountItems()} 道菜 · {app.GetSettings().KitchenPrinterName}");

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText = DateTime.Now.ToString("HH:mm:ss  ddd d MMM");
        _clock.Start();
        ClockText = DateTime.Now.ToString("HH:mm:ss  ddd d MMM");

        void OnCall(object? _, CallerIdEventArgs e) =>
            Dispatcher.UIThread.Post(() =>
            {
                Till.ApplyCallerId(e.PhoneNumber);
                var who = string.IsNullOrWhiteSpace(e.CallerName)
                    ? e.PhoneNumber
                    : $"{e.CallerName} · {e.PhoneNumber}";
                StatusText = UiText.Pick($"Caller ID: {who}", $"来电: {who}");
                GoTill();
            });

        app.CallerIdProvider.CallReceived += OnCall;

        // The simulator is also subscribed when it is not the live provider, so
        // the test call in Settings still reaches the screen on a till that has
        // a real box attached.
        if (!ReferenceEquals(app.CallerIdProvider, app.CallerId))
            app.CallerId.CallReceived += OnCall;

        if (app.CallerIdProvider is SerialModemCallerId serial)
        {
            // A withheld number is not a fault and must not look like one: the
            // phone is ringing, there is simply nobody to look up.
            serial.WithheldCallReceived += (_, reason) =>
                Dispatcher.UIThread.Post(() => StatusText = reason == CallerIdWithheld.Private
                    ? UiText.Pick("Incoming call — number withheld", "来电 — 对方隐藏号码")
                    : UiText.Pick("Incoming call — number unavailable", "来电 — 无法获取号码"));
        }

        app.OnlinePoller.OrderReceived += async (_, order) =>
        {
            try
            {
                await app.Print.HandleOnlineOrderAsync(order);
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = UiText.Pick(
                        $"Web order {order.OrderNumber} · kitchen printed",
                        $"网单 {order.OrderNumber} · 已打厨房");

                    // A banner that stays until someone looks. A message that
                    // clears itself is no use to a kitchen at seven o'clock.
                    NewWebOrders++;
                    NewWebOrderBanner = UiText.Pick(
                        NewWebOrders == 1
                            ? $"New web order {order.OrderNumber}"
                            : $"{NewWebOrders} new web orders",
                        NewWebOrders == 1
                            ? $"新网单 {order.OrderNumber}"
                            : $"{NewWebOrders} 张新网单");
                    HasNewWebOrders = true;
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

    public TillViewModel Till { get; }
    public OrdersViewModel Orders { get; }
    public CustomersViewModel Customers { get; }
    public DispatchViewModel Dispatch { get; }
    public SettingsViewModel SettingsVm { get; }

    [ObservableProperty] private bool _isLocked = true;
    [ObservableProperty] private string _pinEntry = "";
    [ObservableProperty] private string _pinMask = "";
    [ObservableProperty] private string _lockMessage = "";
    [ObservableProperty] private bool _hasLockMessage;
    [ObservableProperty] private string _staffName = "";
    [ObservableProperty] private string _shiftLabel = "";
    [ObservableProperty] private string _noShiftLabel = "No shift";
    [ObservableProperty] private bool _hasOpenShift;
    [ObservableProperty] private bool _webOn;
    [ObservableProperty] private string _webLabel = "Web off";
    [ObservableProperty] private string _webTooltip = "";
    [ObservableProperty] private bool _printersHealthy = true;
    [ObservableProperty] private string _printerLabel = "";
    [ObservableProperty] private string _lblLock = "Lock";
    [ObservableProperty] private string _lblSignIn = "Sign in";
    [ObservableProperty] private string _lblEnterPin = "Enter your PIN";
    [ObservableProperty] private string _lblOpenShift = "Open shift";
    [ObservableProperty] private string _lblCloseShift = "Close shift";

    [ObservableProperty] private ViewModelBase _currentPage = null!;
    [ObservableProperty] private string _sectionTitle = "Till";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _clockText = "";
    [ObservableProperty] private string _shopName = "";
    [ObservableProperty] private string _navKey = "till";
    [ObservableProperty] private int _newWebOrders;
    [ObservableProperty] private bool _hasNewWebOrders;
    [ObservableProperty] private string _newWebOrderBanner = "";
    [ObservableProperty] private string _navTill = "Till";
    [ObservableProperty] private string _navOrders = "Orders";
    [ObservableProperty] private string _navCustomers = "Customers";
    [ObservableProperty] private string _navSettings = "Settings";
    [ObservableProperty] private string _lblLanguage = "中文";
    [ObservableProperty] private string _lblDrawer = "Drawer";
    [ObservableProperty] private string _languageHint = "";
    [ObservableProperty] private bool _isTillNav = true;
    [ObservableProperty] private bool _isOrdersNav;
    [ObservableProperty] private bool _isCustomersNav;
    [ObservableProperty] private bool _isDispatchNav;

    /// <summary>
    /// Whether this shop sends its own drivers out. Derived from whether anyone
    /// is graded a driver, so a merchant whose deliveries all go through Uber
    /// Eats never sees a screen about drivers, and one who hires a driver gets
    /// it by adding them in Settings.
    /// </summary>
    [ObservableProperty] private bool _showDispatchNav;
    [ObservableProperty] private string _navDispatch = "Delivery";
    [ObservableProperty] private bool _isSettingsNav;

    [RelayCommand]
    private void GoTill() => Navigate("till", Till, UiText.NavTill, () =>
    {
        Till.RefreshMenu();
        Till.RefreshUiLabels();
        Till.RefreshHeldList();
    });

    [RelayCommand]
    private void GoOrders() => Navigate("orders", Orders, UiText.NavOrders, () =>
    {
        // Looking at the list is the acknowledgement.
        NewWebOrders = 0;
        HasNewWebOrders = false;
        Orders.RefreshUiLabels();
        Orders.Refresh();
    });

    [RelayCommand]
    private void GoCustomers() => Navigate("customers", Customers, UiText.NavCustomers, () =>
    {
        Customers.RefreshUiLabels();
        Customers.Refresh();
    });

    [RelayCommand]
    private void GoDispatch() => Navigate("dispatch", Dispatch, UiText.NavDispatch, Dispatch.Refresh);

    [RelayCommand]
    private void GoSettings() => Navigate("settings", SettingsVm, UiText.NavSettings, () =>
    {
        SettingsVm.RefreshUiLabels();
        SettingsVm.Reload();
        ShopName = _app.GetSettings().ShopName;
    });

    private void Navigate(string key, ViewModelBase page, string title, Action? onEnter = null)
    {
        if (NavKey == "till" && key != "till")
            Till.CompletePendingSettlement();

        NavKey = key;
        IsTillNav = key == "till";
        IsOrdersNav = key == "orders";
        IsCustomersNav = key == "customers";
        IsDispatchNav = key == "dispatch";
        IsSettingsNav = key == "settings";
        SectionTitle = title;
        CurrentPage = page;
        onEnter?.Invoke();
    }

    [RelayCommand]
    private async Task OpenDrawerAsync()
    {
        if (!await UiPrompt.RequireAsync(_app, Permission.OpenDrawerWithoutSale, UiText.Pick("Open drawer", "开钱箱")))
            return;
        try
        {
            await _app.Print.OpenDrawerAsync();
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
        if (IsTillNav) GoTill();
        else if (IsOrdersNav) GoOrders();
        else if (IsCustomersNav) GoCustomers();
        else GoSettings();
    }

    private void RefreshAllUiLanguage()
    {
        NavTill = UiText.NavTill;
        NavOrders = UiText.NavOrders;
        NavCustomers = UiText.NavCustomers;
        NavDispatch = UiText.NavDispatch;
        // Derived *and* permitted. The shop having drivers is what makes the
        // board meaningful; the entitlement is what makes it something we sell.
        // Either alone is the wrong answer.
        ShowDispatchNav =
            _app.Dispatch.ShopUsesOwnDrivers()
            && _app.Entitlement.Current.Allows(ShopFeatures.Drivers);
        NavSettings = UiText.NavSettings;
        LblLanguage = UiText.LanguageToggle;
        LblDrawer = UiText.Drawer;
        LblLock = UiText.Lock;
        LblSignIn = UiText.SignIn;
        LblEnterPin = UiText.EnterPin;
        LblOpenShift = UiText.Pick("Open shift", "开班");
        LblCloseShift = UiText.Pick("Close shift and count the drawer", "关班点钞");
        LanguageHint = UiText.UiLangNote;
        Till.RefreshUiLabels();
        Orders.RefreshUiLabels();
        Customers.RefreshUiLabels();
        SettingsVm.RefreshUiLabels();
    }

    public void SetStatus(string text) => StatusText = text;

    // ── Sign in / lock ──────────────────────────────────────────────────────

    [RelayCommand]
    private void PinDigit(string? digit)
    {
        if (string.IsNullOrEmpty(digit) || PinEntry.Length >= 8) return;
        PinEntry += digit;
        HasLockMessage = false;
    }

    [RelayCommand]
    private void PinBackspace()
    {
        if (PinEntry.Length > 0) PinEntry = PinEntry[..^1];
        HasLockMessage = false;
    }

    [RelayCommand]
    private void SignIn()
    {
        if (PinEntry.Length == 0) return;

        var member = _app.Session.SignIn(PinEntry);
        PinEntry = "";

        if (member is null)
        {
            LockMessage = UiText.Pick("PIN not recognised", "PIN 不正确");
            HasLockMessage = true;
            return;
        }

        HasLockMessage = false;
        IsLocked = false;
        RefreshSession();

        StatusText = member.MustChangePin
            ? UiText.Pick(
                $"Signed in as {member.Name} — change this PIN in Settings",
                $"已登录：{member.Name} — 请到设置中修改 PIN")
            : UiText.Pick($"Signed in as {member.Name}", $"已登录：{member.Name}");
    }

    [RelayCommand]
    private void Lock()
    {
        Till.CompletePendingSettlement();
        _app.Session.SignOut();
        PinEntry = "";
        IsLocked = true;
        RefreshSession();
    }

    partial void OnPinEntryChanged(string value) => PinMask = new string('●', value.Length);

    private void RefreshSession()
    {
        var session = _app.Session;
        StaffName = session.Staff?.Name ?? "";
        HasOpenShift = session.HasOpenShift;
        ShiftLabel = session.Shift is { } shift
            ? UiText.Pick($"Shift {shift.Number}", $"班次 {shift.Number}")
            : "";
        NoShiftLabel = UiText.Pick("No shift open", "未开班");
        RefreshStatusLights();
    }

    /// <summary>
    /// Printers and the web feed fail quietly. A light that is already wrong
    /// before anyone reaches for a ticket is the cheapest fix there is.
    /// </summary>
    private void RefreshStatusLights()
    {
        // Reads the device registry, and counts a printer as unhealthy if the
        // queue reported a fault on it — a printer that answers but keeps
        // rejecting tickets is not working, whatever the connection says.
        var devices = _app.PrintDevices.GetDevices(enabledOnly: true);
        var faults = _app.PrintQueue.Faults;
        var working = devices.Count(d => !faults.ContainsKey(d.Id) && IsReachable(d));

        PrintersHealthy = devices.Count > 0 && working == devices.Count;
        PrinterLabel = devices.Count == 0
            ? UiText.Pick("No printer", "未设打印机")
            : $"{working}/{devices.Count}";

        var waiting = _app.PrintJobs.CountWaiting();
        if (waiting > 0)
            PrinterLabel += UiText.Pick($" · {waiting} waiting", $" · {waiting} 待打");

        WebOn = _app.OnlinePoller.IsRunning;
        WebLabel = WebOn
            ? UiText.Pick("Web on", "网单：开")
            : UiText.Pick("Web off", "网单：关");
        WebTooltip = string.IsNullOrWhiteSpace(_app.OnlinePoller.LastStatus)
            ? UiText.Pick("Pull orders from the shop website", "从店铺网站拉取订单")
            : _app.OnlinePoller.LastStatus;
    }

    /// <summary>
    /// Cheap enough for a status light. Only the Windows queue is checked
    /// synchronously; network and serial devices are judged by whether the
    /// queue has been able to print to them, because a two-second connect
    /// attempt has no business running on the UI thread.
    /// </summary>
    private static bool IsReachable(PrintDevice device) =>
        device.Transport != PrintTransport.WindowsQueue || RawPrinter.CanOpen(device.Address);

    [RelayCommand]
    private async Task ToggleWebOrdersAsync()
    {
        try
        {
            var settings = _app.GetSettings();
            if (_app.OnlinePoller.IsRunning)
            {
                await _app.OnlinePoller.StopAsync();
                settings.OnlinePollingEnabled = false;
                _app.SaveSettings(settings);
                StatusText = UiText.Pick("Web orders paused", "已暂停接网单");
            }
            else
            {
                settings.OnlinePollingEnabled = true;
                _app.SaveSettings(settings);
                await _app.OnlinePoller.StartAsync();
                StatusText = UiText.Pick("Accepting web orders", "开始接网单");
            }
        }
        catch (Exception ex)
        {
            StatusText = UiText.Pick($"Web orders: {ex.Message}", $"网单：{ex.Message}");
        }

        RefreshStatusLights();
    }

    // ── Shift ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenShiftAsync()
    {
        if (!_app.Session.IsSignedIn) return;

        var entered = await UiPrompt.PromptTextAsync(
            UiText.Pick("Open shift", "开班"),
            UiText.Pick("Opening float in the drawer, e.g. 100.00", "备用金，例如 100.00"),
            initial: "0.00");
        if (entered is null) return;
        if (!decimal.TryParse(entered.Trim(), out var openingFloat) || openingFloat < 0)
        {
            StatusText = UiText.Pick("Opening float must be a number", "备用金必须是数字");
            return;
        }

        var shift = _app.Session.OpenShift(openingFloat);
        RefreshSession();
        StatusText = UiText.Pick(
            $"Shift {shift.Number} open with £{openingFloat:0.00} float",
            $"班次 {shift.Number} 已开，备用金 £{openingFloat:0.00}");
    }

    [RelayCommand]
    private async Task CloseShiftAsync()
    {
        if (_app.Session.Shift is not { } shift) return;
        if (!_app.Session.Can(Permission.CloseShift))
        {
            StatusText = UiText.Pick("Closing a shift needs a supervisor", "关班需要主管权限");
            return;
        }

        var totals = _app.Session.CurrentTotals()!;
        if (totals.OrdersOpen > 0 && !await UiPrompt.ConfirmAsync(
                UiText.Pick("Close shift?", "关班？"),
                UiText.Pick(
                    $"{totals.OrdersOpen} order(s) still owe £{totals.OutstandingDue:0.00}. Close anyway?",
                    $"仍有 {totals.OrdersOpen} 单未结，共 £{totals.OutstandingDue:0.00}。仍要关班？")))
            return;

        // The count is entered before the expected figure is shown: a till that
        // volunteers the answer first is not counting the drawer, it is
        // confirming it.
        var counted = await UiPrompt.PromptTextAsync(
            UiText.Pick($"Close shift {shift.Number}", $"关班 {shift.Number}"),
            UiText.Pick("Cash counted in the drawer", "点出的现金金额"));
        if (counted is null) return;
        if (!decimal.TryParse(counted.Trim(), out var declared) || declared < 0)
        {
            StatusText = UiText.Pick("Counted cash must be a number", "点钞金额必须是数字");
            return;
        }

        var (closed, variance) = _app.Session.CloseShift(declared, null);
        RefreshSession();

        var verdict = variance == 0
            ? UiText.Pick("balances", "平账")
            : variance > 0
                ? UiText.Pick($"over by £{variance:0.00}", $"多 £{variance:0.00}")
                : UiText.Pick($"short by £{-variance:0.00}", $"少 £{-variance:0.00}");

        StatusText = UiText.Pick(
            $"Shift {shift.Number} closed — expected £{closed.ExpectedCash:0.00}, counted £{declared:0.00}, {verdict}",
            $"班次 {shift.Number} 已关 — 应有 £{closed.ExpectedCash:0.00}，实点 £{declared:0.00}，{verdict}");

        // The Z reading is the point of closing a shift, so it goes on paper
        // without being asked for. Queued, never awaited: a printer out of paper
        // must not leave a shift half-closed, and the reading is reproducible
        // from the rows for as long as they exist.
        try
        {
            var queued = await _app.Print.PrintShiftReportAsync(_app.ShiftReports.BuildZ(shift));
            StatusText += queued > 0
                ? UiText.Pick(" · Z reading printing", " · Z 报表打印中")
                : UiText.Pick(" · no printer for the Z reading", " · 无打印机，Z 报表未打");
        }
        catch (Exception ex)
        {
            // Reported, never fatal. The shift is closed and the money is
            // counted; the paper can be reprinted from Settings.
            AppLog.Error("shift", $"Z reading for shift {shift.Number} failed to queue", ex);
            StatusText += UiText.Pick(" · Z reading failed — reprint from Settings",
                " · Z 报表失败 — 可在设置中补打");
        }
    }

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
