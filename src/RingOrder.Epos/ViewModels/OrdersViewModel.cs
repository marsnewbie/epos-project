using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Online;
using RingOrder.Epos.Services;

namespace RingOrder.Epos.ViewModels;

public partial class OrdersViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly Action<string> _setStatus;
    private readonly Action<PosOrder>? _openOnSell;

    public OrdersViewModel(AppServices app, Action<string> setStatus, Action<PosOrder>? openOnSell = null)
    {
        _app = app;
        _setStatus = setStatus;
        _openOnSell = openOnSell;
    }

    public ObservableCollection<PosOrder> Orders { get; } = [];

    [ObservableProperty] private PosOrder? _selectedOrder;
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private bool _filterAll = true;
    [ObservableProperty] private bool _filterUnpaid;
    [ObservableProperty] private bool _filterHeld;
    [ObservableProperty] private bool _filterPaid;
    [ObservableProperty] private string _todaySummary = "";
    [ObservableProperty] private string _lblToday = "Today";
    [ObservableProperty] private string _lblRefresh = "Refresh";
    [ObservableProperty] private string _lblFilterAll = "All";
    [ObservableProperty] private string _lblFilterUnpaid = "Unpaid";
    [ObservableProperty] private string _lblFilterHeld = "Held";
    [ObservableProperty] private string _lblFilterPaid = "Paid";
    [ObservableProperty] private string _lblDetail = "Order detail";
    [ObservableProperty] private string _lblOpenOnSell = "Open on the till";
    [ObservableProperty] private string _lblReprintKitchen = "Reprint kitchen";
    [ObservableProperty] private string _lblReprintFront = "Reprint receipt";
    [ObservableProperty] private string _lblVoid = "Void (PIN)";
    [ObservableProperty] private string _lblReopen = "Reopen (PIN)";

    public void RefreshUiLabels()
    {
        LblToday = UiText.Today;
        LblRefresh = UiText.Refresh;
        LblFilterAll = UiText.FilterAll;
        LblFilterUnpaid = UiText.FilterUnpaid;
        LblFilterHeld = UiText.FilterHeld;
        LblFilterPaid = UiText.FilterPaid;
        LblDetail = UiText.OrderDetail;
        LblOpenOnSell = UiText.OpenOnSell;
        LblReprintKitchen = UiText.ReprintKitchen;
        LblReprintFront = UiText.ReprintFront;
        LblVoid = UiText.VoidOrder;
        LblReopen = UiText.ReopenOrder;
        Refresh();
    }

    public void Refresh()
    {
        Orders.Clear();
        foreach (var o in _app.Orders.GetTodayFiltered(Filter))
            Orders.Add(o);
        if (SelectedOrder is not null)
            SelectedOrder = Orders.FirstOrDefault(o => o.Id == SelectedOrder.Id);

        var all = _app.Orders.GetToday();
        var active = all.Where(o => o.Status is not (PosOrderStatus.Voided or PosOrderStatus.Cancelled)).ToList();
        var paidDone = active.Where(o => o.Status is PosOrderStatus.Paid or PosOrderStatus.Completed).ToList();
        // Include partial tenders on Sent/Held (cash already in drawer)
        var cash = active.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.Cash).Sum(t => t.Amount);
        var card = active.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.CardManual).Sum(t => t.Amount);
        var online = active.SelectMany(o => o.Tenders).Where(t => t.Type == TenderType.PrepaidOnline).Sum(t => t.Amount);
        var dueOpen = active.Where(o => o.IsUnpaid).Sum(o => o.BalanceDue);
        var unpaid = active.Count(o => o.IsUnpaid && o.Status != PosOrderStatus.Held);
        var held = active.Count(o => o.Status == PosOrderStatus.Held);
        TodaySummary = UiText.Pick(
            $"Taken: Cash £{cash:0.00} · Card £{card:0.00} · Online £{online:0.00} · Open due £{dueOpen:0.00} · Paid tickets {paidDone.Count} · Unpaid {unpaid} · Held {held}",
            $"已收：现金 £{cash:0.00} · 刷卡 £{card:0.00} · 线上 £{online:0.00} · 未结待收 £{dueOpen:0.00} · 付清单 {paidDone.Count} · 未付 {unpaid} · 挂单 {held}");
    }

    [RelayCommand]
    private void SetFilter(string? filter)
    {
        Filter = filter ?? "All";
        FilterAll = Filter.Equals("All", StringComparison.OrdinalIgnoreCase);
        FilterUnpaid = Filter.Equals("Unpaid", StringComparison.OrdinalIgnoreCase);
        FilterHeld = Filter.Equals("Held", StringComparison.OrdinalIgnoreCase);
        FilterPaid = Filter.Equals("Paid", StringComparison.OrdinalIgnoreCase);
        Refresh();
    }

    partial void OnSelectedOrderChanged(PosOrder? value)
    {
        if (value is null)
        {
            DetailText = "";
            return;
        }

        var lines = string.Join("\n", value.Lines.Select(l =>
            $"{l.Quantity}x {l.Name} £{l.LineTotal:0.00}" +
            (l.KitchenSent ? " [SENT]" : " [NEW]") +
            (string.IsNullOrWhiteSpace(l.Notes) ? "" : $" ({l.Notes})")));
        var tenders = value.Tenders.Count == 0
            ? UiText.Pick("No payments yet", "尚未收款")
            : string.Join("\n", value.Tenders.Select(t =>
                $"  {t.Type} £{t.Amount:0.00}" +
                (t.CashReceived is > 0 ? $" (tendered £{t.CashReceived:0.00})" : "") +
                (t.ChangeGiven is > 0 ? $" change £{t.ChangeGiven:0.00}" : "")));
        DetailText =
            $"{value.OrderNumber}  {value.ServiceType}  {value.Channel}  {value.Status}\n" +
            (string.IsNullOrWhiteSpace(value.HoldLabel) ? "" : $"Hold: {value.HoldLabel}\n") +
            (string.IsNullOrWhiteSpace(value.TableNumber) ? "" : $"Table: {value.TableNumber}\n") +
            $"{value.CustomerName} {value.CustomerPhone}\n" +
            $"{value.DeliveryAddress} {value.DeliveryPostcode}\n" +
            $"{lines}\n" +
            $"Subtotal £{value.Subtotal:0.00}  Delivery £{value.DeliveryFee:0.00}  Total £{value.Total:0.00}\n" +
            $"Paid £{value.AmountPaid:0.00}  Due £{value.BalanceDue:0.00}\n" +
            $"{tenders}\n" +
            $"Kitchen={(value.KitchenPrinted ? "Y" : "N")} Front={(value.FrontPrinted ? "Y" : "N")}\n" +
            $"Notes: {value.Notes}" +
            (string.IsNullOrWhiteSpace(value.VoidReason) ? "" : $"\nVoid: {value.VoidReason}");
    }

    [RelayCommand]
    private void OpenOnSell()
    {
        if (SelectedOrder is null) return;
        if (SelectedOrder.Status is PosOrderStatus.Voided)
        {
            _setStatus(UiText.Pick("Voided — cannot open", "已作废，无法打开"));
            return;
        }
        if (SelectedOrder.Status is PosOrderStatus.Paid or PosOrderStatus.Completed)
        {
            _setStatus(UiText.Pick("Fully paid — use Reopen (PIN) to add items", "已付清 — 用「重开加菜」继续"));
            return;
        }
        _openOnSell?.Invoke(SelectedOrder);
    }

    /// <summary>Industry: reopen a paid ticket (manager PIN) to add items and collect the new balance.</summary>
    [RelayCommand]
    private async Task ReopenOrderAsync()
    {
        if (SelectedOrder is null) return;
        if (SelectedOrder.Status is PosOrderStatus.Voided)
        {
            _setStatus(UiText.Pick("Voided — cannot reopen", "已作废，无法重开"));
            return;
        }
        if (SelectedOrder.Status is not (PosOrderStatus.Paid or PosOrderStatus.Completed))
        {
            // Unpaid — just open
            _openOnSell?.Invoke(SelectedOrder);
            return;
        }
        if (!await UiPrompt.RequireAsync(_app, Permission.ReopenPaidOrder, UiText.Pick("Reopen a paid order", "重开已付订单")))
            return;
        if (!await UiPrompt.ConfirmAsync(
                UiText.Pick("Reopen paid order?", "重开已付订单？"),
                UiText.Pick(
                    $"Reopen {SelectedOrder.OrderNumber}? Previous payments £{SelectedOrder.AmountPaid:0.00} stay on the ticket. New dishes create a balance due.",
                    $"重开 {SelectedOrder.OrderNumber}？已付 £{SelectedOrder.AmountPaid:0.00} 保留，新加菜产生待收款。")))
            return;

        SelectedOrder.Status = PosOrderStatus.Sent;
        SelectedOrder.UpdatedAt = DateTimeOffset.Now;
        // Keep tenders; if still fully paid until dishes added, IsUnpaid stays false
        _app.Orders.Upsert(SelectedOrder);
        _setStatus(UiText.Pick(
            $"Reopened {SelectedOrder.OrderNumber} — add items, then collect balance",
            $"已重开 {SelectedOrder.OrderNumber} — 可加菜，再收尾款"));
        _openOnSell?.Invoke(SelectedOrder);
        Refresh();
    }

    [RelayCommand]
    private async Task VoidOrderAsync()
    {
        if (SelectedOrder is null) return;
        if (SelectedOrder.Status is PosOrderStatus.Voided)
        {
            _setStatus("Already voided");
            return;
        }
        if (!await UiPrompt.RequireAsync(_app, Permission.VoidOrder, UiText.Pick("Void order", "作废订单")))
            return;
        var paidNote = SelectedOrder.AmountPaid > 0
            ? UiText.Pick(
                $" WARNING: £{SelectedOrder.AmountPaid:0.00} already taken — refund customer manually if needed.",
                $" 注意：已收 £{SelectedOrder.AmountPaid:0.00} — 如需退款请人工处理。")
            : "";
        var reason = await UiPrompt.PromptTextAsync(
            UiText.Pick("Void reason", "作废原因") + paidNote,
            UiText.Pick("Reason", "原因"),
            initial: "Staff error");
        if (reason is null) return;
        try
        {
            var printVoid = _app.GetSettings().PrintVoidKitchenTicket;
            await _app.Print.VoidOrderAsync(SelectedOrder, reason.Trim(), printVoid);
            _setStatus($"Voided {SelectedOrder.OrderNumber}");
            Refresh();
        }
        catch (Exception ex)
        {
            _setStatus($"Void failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ReprintKitchenAsync()
    {
        if (SelectedOrder is null) return;
        try
        {
            await _app.Print.PrintKitchenAsync(SelectedOrder, isReprint: true);
            _setStatus($"Reprinted kitchen {SelectedOrder.OrderNumber}");
            Refresh();
        }
        catch (Exception ex)
        {
            _setStatus($"Reprint kitchen failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ReprintFrontAsync()
    {
        if (SelectedOrder is null) return;
        try
        {
            await _app.Print.PrintFrontAsync(SelectedOrder);
            _setStatus($"Reprinted front {SelectedOrder.OrderNumber}");
            Refresh();
        }
        catch (Exception ex)
        {
            _setStatus($"Reprint front failed: {ex.Message}");
        }
    }
}
