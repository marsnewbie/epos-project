using Avalonia.Controls;
using Avalonia.Interactivity;
using RingOrder.Epos.ViewModels;

namespace RingOrder.Epos.Views;

public partial class OrdersView : UserControl
{
    public OrdersView() => InitializeComponent();

    private void OnRefresh(object? sender, RoutedEventArgs e)
        => (DataContext as OrdersViewModel)?.Refresh();
}
