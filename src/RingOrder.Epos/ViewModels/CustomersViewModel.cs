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
    }

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
        if (!string.IsNullOrWhiteSpace(EditAddress) || !string.IsNullOrWhiteSpace(EditPostcode))
        {
            c.Addresses =
            [
                new CustomerAddress
                {
                    Line1 = EditAddress.Trim(),
                    Postcode = EditPostcode.Trim(),
                    IsDefault = true,
                }
            ];
        }
        _app.Customers.Upsert(c);
        Refresh();
        SelectedCustomer = Customers.FirstOrDefault(x => x.Id == c.Id);
        _setStatus($"Saved customer {c.Name}");
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
