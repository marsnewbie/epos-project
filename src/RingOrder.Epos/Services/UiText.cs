using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Services;

/// <summary>
/// Chrome / button language only. Menu dish names stay on the catalogue
/// (English front + kitchen Chinese) and are never flipped by UiLanguage.
/// </summary>
public static class UiText
{
    public static bool IsZh =>
        string.Equals(AppServices.Instance.GetSettings().UiLanguage, "zh", StringComparison.OrdinalIgnoreCase);

    public static string Pick(string en, string zh) => IsZh ? zh : en;

    public static string Pick(AppSettings settings, string en, string zh) =>
        string.Equals(settings.UiLanguage, "zh", StringComparison.OrdinalIgnoreCase) ? zh : en;

    // ── Shell ──────────────────────────────────────────────────────
    public static string LanguageToggle => IsZh ? "English" : "中文";
    public static string Drawer => Pick("Drawer", "钱箱");
    public static string Lock => Pick("Lock", "锁屏");
    public static string SignIn => Pick("Sign in", "登录");
    public static string EnterPin => Pick("Enter your PIN", "请输入 PIN");

    // Rail labels. "Till" is what the trade calls the screen you take an order
    // and take the money on, and it does not collide with "Orders" the way
    // "Order" would.
    public static string NavSell => Pick("Till", "收银台");
    public static string NavOrders => Pick("Orders", "订单");
    public static string NavOnline => Pick("Web orders", "网单");
    public static string NavCustomers => Pick("Customers", "顾客");
    public static string NavSettings => Pick("Settings", "设置");

    // ── Dialogs ────────────────────────────────────────────────────
    public static string Ok => Pick("OK", "确定");
    public static string Cancel => Pick("Cancel", "取消");
    public static string ApprovalTitle(string action) =>
        Pick($"Approval needed · {action}", $"需要授权 · {action}");
    public static string PinIncorrectTitle => Pick("PIN not recognised", "PIN 不正确");
    public static string PinIncorrectBody =>
        Pick("No staff member has that PIN.", "没有员工使用该 PIN。");
    public static string NotAllowedTitle => Pick("Not allowed", "权限不足");
    public static string NotAllowedBody(string name, string action) =>
        Pick($"{name} is not allowed to {action.ToLowerInvariant()}.",
             $"{name} 没有「{action}」的权限。");

    // ── Sell ───────────────────────────────────────────────────────
    public static string Search => Pick("Search dishes", "搜索菜品");
    public static string DishNumber => Pick("Menu #", "菜号");
    public static string AddByNumber => Pick("Add", "添加");
    public static string PhoneOrder => Pick("Phone order", "电话单");
    public static string AdHoc => Pick("Custom item", "临时菜");
    public static string AdHocName => Pick("Item name", "名称");
    public static string Notes => Pick("Notes", "备注");
    public static string OrderNotes => Pick("Order notes", "整单备注");
    public static string CustomerName => Pick("Name", "姓名");
    public static string CustomerPhone => Pick("Phone", "电话");
    public static string Address => Pick("Address", "地址");
    public static string Postcode => Pick("Postcode", "邮编");
    public static string TablePager => Pick("Table / pager #", "桌号 / 叫号");
    public static string TypeCollection => Pick("Collect", "外带");
    public static string TypeDelivery => Pick("Deliver", "外卖");
    public static string TypeWaiting => Pick("Waiting", "等取");
    public static string TypeTable => Pick("Table", "堂食");
    public static string NewTicket => Pick("New", "新单");
    public static string ClearTicket => Pick("Clear", "清空");
    public static string Held => Pick("Held", "挂单");
    public static string Hold => Pick("Hold", "挂起");
    public static string Cash => Pick("Cash", "现金");
    public static string Card => Pick("Card", "刷卡");
    public static string TakeCash => Pick("Take cash", "确认收款");
    public static string Back => Pick("Back", "返回");
    public static string AddToTicket => Pick("Add to ticket", "加入订单");
    public static string SendKitchen => Pick("Send kitchen", "送厨");
    public static string SendNew => Pick("Send new", "补打厨房");
    public static string EmptyTicket => Pick("Tap dishes to build the ticket", "点菜开始建单");
    public static string CashTender => Pick("Cash tendered", "收到现金");
    public static string Exact => Pick("Exact", "刚好");
    public static string BalanceDue => Pick("Due", "待收");
    public static string AmountPaid => Pick("Paid", "已付");
    public static string Change => Pick("Change", "找零");
    public static string TakeCashFull => Pick("Take cash", "确认收款");
    public static string PayPartial => Pick("Pay partial", "收部分款");
    public static string PayCardBalance => Pick("Card (balance)", "刷卡收尾款");
    public static string ReopenOrder => Pick("Reopen (PIN)", "重开加菜 (PIN)");
    public static string ClearCash => Pick("CLR", "清空");
    public static string Backspace => "⌫";
    public static string ItemsCount(int n) => Pick($"{n} items", $"{n} 项");
    public static string TicketLabel => Pick("TICKET", "订单");
    public static string LineSent => Pick("SENT", "已送");
    public static string StatusDraft => Pick("DRAFT", "草稿");
    public static string StatusSent => Pick("SENT", "已送厨");
    public static string StatusHeld => Pick("HELD", "挂单");
    public static string StatusPaid => Pick("PAID", "已付");
    public static string StatusVoid => Pick("VOID", "作废");

    // ── Orders ─────────────────────────────────────────────────────
    public static string Today => Pick("Today", "今日");
    public static string Refresh => Pick("Refresh", "刷新");
    public static string FilterAll => Pick("All", "全部");
    public static string FilterUnpaid => Pick("Unpaid", "未付");
    public static string FilterHeld => Pick("Held", "挂单");
    public static string FilterPaid => Pick("Paid", "已付");
    public static string OrderDetail => Pick("Order detail", "订单详情");
    public static string OpenOnSell => Pick("Open on Sell", "续单改菜");
    public static string ReprintKitchen => Pick("Reprint kitchen", "重打厨房");
    public static string ReprintFront => Pick("Reprint receipt", "重打小票");
    public static string VoidOrder => Pick("Void (PIN)", "作废 (PIN)");

    // ── Online ─────────────────────────────────────────────────────
    public static string OnlineToggleOn => Pick("Accepting ON", "接单：开");
    public static string OnlineToggleOff => Pick("Accepting OFF", "接单：关");
    public static string OnlineToggle => Pick("Toggle accepting", "开关接单");
    public static string Advanced => Pick("Advanced", "高级");
    public static string PollOnce => Pick("Poll once", "拉取一次");
    public static string TestConnection => Pick("Test connection", "测试连接");
    public static string AckPrinted => Pick("Ack printed", "确认已打");
    public static string OnlineOrders => Pick("Online orders", "线上订单");
    public static string Detail => Pick("Detail", "详情");
    public static string SetupNeeded => Pick("Setup needed", "需要配置");
    public static string AcceptingYes => Pick("Accepting: ON", "接单中：开");
    public static string AcceptingNo => Pick("Accepting: OFF", "接单中：关");

    // ── Customers ──────────────────────────────────────────────────
    public static string PhoneBook => Pick("Phone book", "电话簿");
    public static string SearchCustomer => Pick("Search name / phone", "搜索姓名 / 电话");
    public static string Customer => Pick("Customer", "顾客");
    public static string SaveCustomer => Pick("Save customer", "保存顾客");
    public static string StartOrder => Pick("Start order", "开单");
    public static string CallerIdSim => Pick("Caller ID simulate", "来电模拟");
    public static string CallerIdHint =>
        Pick("Fills Sell ticket with matched customer", "匹配顾客并填入点单");
    public static string SimulateCall => Pick("Simulate incoming call", "模拟来电");

    // ── Settings sections ──────────────────────────────────────────
    public static string SecShop => Pick("Shop", "店铺");
    public static string SecMenu => Pick("Menu", "菜单");
    public static string SecNotes => Pick("Quick notes", "快捷备注");
    public static string SecDelivery => Pick("Delivery", "外卖费");
    public static string SecHardware => Pick("Hardware", "硬件");
    public static string SecStaff => Pick("Staff / PIN", "员工 / PIN");
    public static string SecShift => Pick("Shift today", "今日班次");
    public static string SecOnline => Pick("Online", "线上");
    public static string SaveSettings => Pick("Save", "保存");
    public static string AddCategory => Pick("+ Category", "+ 分类");
    public static string Edit => Pick("Edit", "编辑");
    public static string HideShow => Pick("Hide/Show", "显隐");
    public static string Delete => Pick("Delete", "删除");
    public static string AddDish => Pick("+ Dish", "+ 菜品");
    public static string SaveDish => Pick("Save dish", "保存菜品");
    public static string Duplicate => Pick("Duplicate", "复制");
    public static string EightySix => Pick("86", "售罄");
    public static string AddGroup => Pick("+ Group", "+ 选项组");
    public static string AddChoice => Pick("+ Choice", "+ 选项");
    public static string Remove => Pick("Remove", "移除");
    public static string Reimport => Pick("Re-import seed", "重导入种子");
    public static string MenuOpsTitle => Pick("Menu operations", "菜单运营");
    public static string MenuOpsHint => Pick(
        "Categories → dishes → option groups → choices (+£ optional). UI language does not change dish names — those stay English + kitchen Chinese.",
        "分类 → 菜品 → 选项组 → 选项（可加价）。界面语言不切换菜名——菜名保持英文前台 + 厨房中文。");
    public static string Categories => Pick("Categories", "分类");
    public static string Dishes => Pick("Dishes", "菜品");
    public static string DishEditor => Pick("Dish editor", "菜品编辑");
    public static string OptionGroups => Pick("Option groups", "选项组");
    public static string Choices => Pick("Choices", "选项");
    public static string UiLangNote => Pick(
        "Top-bar language switches buttons and screens only. Menu catalogue language is separate (English name + kitchen Chinese).",
        "顶部语言只切换按钮与界面文案。菜单目录语言独立（英文菜名 + 厨房中文）。");
}
