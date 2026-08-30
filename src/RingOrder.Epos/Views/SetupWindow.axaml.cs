using Avalonia.Controls;

namespace RingOrder.Epos.Views;

/// <summary>
/// Holds <see cref="SetupView"/> for the one start where a till does not yet
/// know which shop it is. Its own window rather than a page inside the till,
/// because it happens once and nothing else on the screen applies yet.
/// </summary>
public partial class SetupWindow : Window
{
    public SetupWindow() => InitializeComponent();
}
