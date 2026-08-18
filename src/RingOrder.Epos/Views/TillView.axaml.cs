using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RingOrder.Epos.ViewModels;

namespace RingOrder.Epos.Views;

public partial class TillView : UserControl
{
    public TillView()
    {
        InitializeComponent();

        // Tunnelling, not bubbling. A digit pressed with the ticket in focus has
        // to reach the dish-number entry before any control on the way decides
        // it meant something else — a ListBox treats a keypress as type-ahead
        // and moves the selection.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TillViewModel till) return;

        // Modified keys belong to whatever the shop has bound them to, and
        // Ctrl+C on a till is still Ctrl+C.
        if (e.KeyModifiers is not KeyModifiers.None) return;

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var resolved = TillShortcuts.Resolve(e.Key, TillShortcuts.IsTextEntry(focused));

        e.Handled = till.ApplyShortcut(resolved);
    }
}
