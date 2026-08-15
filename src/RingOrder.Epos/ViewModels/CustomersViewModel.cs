using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingOrder.Epos.Domain;
using RingOrder.Epos.Services;

namespace RingOrder.Epos.ViewModels;

public partial class CustomersViewModel : ViewModelBase
{
    private readonly AppServices _app;
    private readonly Action<string> _setStatus;
    private readonly Action<Customer>? _startOrder;

    public CustomersViewModel(AppServices app, Action<string> setStatus, Action<Customer>? startOrder = null)
    {
        _app = app;
        _setStatus = setStatus;
        _startOrder = startOrder;
        AddressLookup = new AddressLookupPanel(app.AddressLookup, () => EditPostcode, candidate =>
        {
            EditAddress = candidate.StreetLine;
            var normalised = UkPostcode.Normalise(candidate.Postcode);
            if (!normalised.IsEmpty) EditPostcode = normalised.Value;
        });
    }

    /// <summary>Postcode → address, beside the address fields.</summary>
    public AddressLookupPanel AddressLookup { get; }

    public ObservableCollection<Customer> Customers { get; } = [];

    [ObservableProperty] private Customer? _selectedCustomer;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editPhone = "";
    [ObservableProperty] private string _editAddress = "";
    [ObservableProperty] private string _editPostcode = "";
    [ObservableProperty] private string _simulatePhone = "01212966775";
    [ObservableProperty] private string _lblTitle = "Phone book";
    [ObservableProperty] private string _wmSearch = "Search name / phone";
    [ObservableProperty] private string _lblCustomer = "Customer";
    [ObservableProperty] private string _wmName = "Name";
    [ObservableProperty] private string _wmPhone = "Phone";
    [ObservableProperty] private string _wmAddress = "Address";
    [ObservableProperty] private string _wmPostcode = "Postcode";
    [ObservableProperty] private string _lblSave = "Save customer";
    [ObservableProperty] private string _lblStartOrder = "Start order";
    [ObservableProperty] private string _lblCallerId = "Caller ID simulate";
    [ObservableProperty] private string _lblCallerHint = "";
    [ObservableProperty] private string _lblSimulate = "Simulate incoming call";

    public void RefreshUiLabels()
    {
        LblTitle = UiText.PhoneBook;
        WmSearch = UiText.SearchCustomer;
        LblCustomer = UiText.Customer;
        WmName = UiText.CustomerName;
        WmPhone = UiText.CustomerPhone;
        WmAddress = UiText.Address;
        WmPostcode = UiText.Postcode;
        LblSave = UiText.SaveCustomer;
        LblStartOrder = UiText.StartOrder;
        LblCallerId = UiText.CallerIdSim;
        LblCallerHint = UiText.CallerIdHint;
        LblSimulate = UiText.SimulateCall;
        AddressLookup.RefreshUiLabels();
    }

    public void Refresh()
    {
        Customers.Clear();
        foreach (var c in string.IsNullOrWhiteSpace(SearchText) ? _app.Customers.ListAll() : _app.Customers.Search(SearchText))
            Customers.Add(c);
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedCustomerChanged(Customer? value)
    {
        if (value is null) return;
        EditName = value.Name;
        EditPhone = value.Phone;
        var addr = value.Addresses.FirstOrDefault();
        EditAddress = addr?.Line1 ?? "";
        EditPostcode = addr?.Postcode ?? "";
        AddressLookup.Reset();
        EraseArmed = false;
    }

    [RelayCommand]
    private void SaveCustomer()
    {
        if (string.IsNullOrWhiteSpace(EditPhone))
        {
            _setStatus("Phone required");
            return;
        }

        var c = SelectedCustomer ?? _app.Customers.FindByPhone(EditPhone) ?? new Customer();
        c.Name = EditName.Trim();
        c.Phone = EditPhone.Trim();
        _app.Customers.Upsert(c);

        // A saved address is a link to a shared place, so the postcode is
        // normalised and the door deduplicated by the repository rather than
        // copied onto this customer's row.
        var found = AddressLookup.StillMatches(EditAddress);

        _app.Customers.SaveAddress(
            c,
            EditAddress,
            line2: null,
            town: found ? AddressLookup.LastPicked!.Town : null,
            EditPostcode,
            found ? AddressSource.Lookup : AddressSource.Manual,
            makeDefault: true,
            latitude: found ? AddressLookup.LastLatitude : null,
            longitude: found ? AddressLookup.LastLongitude : null);
        Refresh();
        SelectedCustomer = Customers.FirstOrDefault(x => x.Id == c.Id);
        _setStatus($"Saved customer {c.Name}");
    }

    /// <summary>
    /// A customer asking to be forgotten. Two presses, because it cannot be
    /// undone — the second press is the confirmation, and it lapses if they walk
    /// away rather than sitting armed for the next person at the till.
    /// </summary>
    [ObservableProperty] private bool _eraseArmed;

    public string LblErase => EraseArmed
        ? UiText.Pick("Press again to erase", "再按一次确认清除")
        : UiText.Pick("Erase customer", "清除客户数据");

    partial void OnEraseArmedChanged(bool value) => OnPropertyChanged(nameof(LblErase));

    [RelayCommand]
    private async Task EraseCustomerAsync()
    {
        if (SelectedCustomer is null)
        {
            _setStatus(UiText.Pick("Pick a customer first", "请先选择客户"));
            return;
        }

        if (!EraseArmed)
        {
            EraseArmed = true;
            _setStatus(UiText.Pick(
                "This cannot be undone. Press again to erase.",
                "此操作不可撤销。再按一次执行清除。"));
            return;
        }

        if (!await UiPrompt.RequireAsync(_app, Permission.EditSettings,
                UiText.Pick("Erase customer data", "清除客户数据")))
        {
            EraseArmed = false;
            return;
        }

        var outcome = _app.Retention.EraseCustomer(SelectedCustomer.Id);

        // The audit line carries counts, never the name that was just removed.
        _app.Session.Record("customers.erased.request", detail: outcome.Summary);
        AppLog.Info("privacy", $"erasure request: {outcome.Summary}");

        EraseArmed = false;
        SelectedCustomer = null;
        EditName = EditPhone = EditAddress = EditPostcode = "";
        Refresh();
        _setStatus(UiText.Pick(
            $"Erased. Orders kept: {outcome.Orders} de-identified.",
            $"已清除。订单保留：{outcome.Orders} 笔已去除身份信息。"));
    }

    [RelayCommand]
    private void StartOrder()
    {
        if (SelectedCustomer is null)
        {
            _setStatus("Select a customer first");
            return;
        }
        _startOrder?.Invoke(SelectedCustomer);
    }

    [RelayCommand]
    private void SimulateCallerId()
    {
        _app.CallerId.Simulate(SimulatePhone.Trim());
        _setStatus($"Simulated CID {SimulatePhone}");
    }
}
