using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Services;

/// <summary>Simple touch-friendly prompts (PIN / confirm / text).</summary>
public static class UiPrompt
{
    private static Window? Owner
    {
        get
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            }
            return null;
        }
    }

    private static IBrush Fg => new SolidColorBrush(Color.Parse("#1c1917"));
    private static IBrush FgMuted => new SolidColorBrush(Color.Parse("#5b6472"));
    private static IBrush Bg => new SolidColorBrush(Color.Parse("#ffffff"));

    public static async Task<bool> ConfirmAsync(string title, string message)
    {
        var owner = Owner;
        if (owner is null) return true;

        var result = false;
        var dlg = new Window
        {
            Title = title,
            Width = 440,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Bg,
        };

        var ok = new Button { Content = UiText.Ok, Width = 140, Height = 48 };
        ok.Classes.Add("cash");
        var cancel = new Button { Content = UiText.Cancel, Width = 120, Height = 48 };
        cancel.Classes.Add("btn");
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();

        dlg.Content = new Border
        {
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Fg },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = FgMuted },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };

        await dlg.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> PromptTextAsync(string title, string watermark, bool password = false, string? initial = null)
    {
        var owner = Owner;
        if (owner is null) return initial;

        string? result = null;
        var box = new TextBox
        {
            Watermark = watermark,
            Text = initial ?? "",
            FontSize = 20,
            MinHeight = 48,
        };
        box.Classes.Add("field");
        if (password) box.PasswordChar = '•';

        var dlg = new Window
        {
            Title = title,
            Width = 420,
            Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Bg,
        };

        var ok = new Button { Content = UiText.Ok, Width = 120, Height = 48 };
        ok.Classes.Add("cash");
        var cancel = new Button { Content = UiText.Cancel, Width = 120, Height = 48 };
        cancel.Classes.Add("btn");
        ok.Click += (_, _) => { result = box.Text; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { result = box.Text; dlg.Close(); }
        };

        dlg.Content = new Border
        {
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Fg },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };

        dlg.Opened += (_, _) => box.Focus();
        await dlg.ShowDialog(owner);
        return result;
    }

    public static async Task<bool> RequireManagerPinAsync(AppSettings settings, string actionLabel)
    {
        var pin = await PromptTextAsync(UiText.ManagerPinTitle(actionLabel), "PIN", password: true);
        if (pin is null) return false;
        if (string.Equals(pin.Trim(), settings.ManagerPin?.Trim() ?? "1234", StringComparison.Ordinal))
            return true;
        await ConfirmAsync(UiText.PinIncorrectTitle, UiText.PinIncorrectBody);
        return false;
    }
}
