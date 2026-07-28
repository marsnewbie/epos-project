using Avalonia.Controls;
using Avalonia.Interactivity;
using MagicWok.Epos.ViewModels;

namespace MagicWok.Epos.Views;

public partial class OrdersView : UserControl
{
    public OrdersView() => InitializeComponent();

    private void OnRefresh(object? sender, RoutedEventArgs e)
        => (DataContext as OrdersViewModel)?.Refresh();
}
