using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
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

    /// <summary>
    /// Gate an action on a permission, with supervisor override.
    /// <para>
    /// The real scene: a cashier is mid-order, the customer wants a line taken
    /// off, and the supervisor walks over. Signing the cashier out and back in
    /// would lose the ticket, so the supervisor types their own PIN, the action
    /// is recorded against both of them, and the cashier keeps the screen.
    /// </para>
    /// <para>
    /// Someone who already holds the permission is not asked again. A till that
    /// challenges a manager for manager work teaches everyone to share a PIN,
    /// which is how audit trails become fiction.
    /// </para>
    /// </summary>
    public static async Task<bool> RequireAsync(AppServices app, Permission permission, string actionLabel)
    {
        var session = app.Session;

        if (session.Can(permission))
        {
            session.Record(AuditAction(permission), detail: actionLabel);
            return true;
        }

        var pin = await PromptPinAsync(UiText.ApprovalTitle(actionLabel));
        if (pin is null) return false;

        var approver = app.Staff.Authenticate(pin);
        if (approver is null)
        {
            await ConfirmAsync(UiText.PinIncorrectTitle, UiText.PinIncorrectBody);
            return false;
        }

        if (!approver.Can(permission))
        {
            await ConfirmAsync(
                UiText.NotAllowedTitle,
                UiText.NotAllowedBody(approver.Name, actionLabel));
            return false;
        }

        session.Record(
            AuditAction(permission),
            subjectId: approver.Id,
            detail: $"{actionLabel} — approved by {approver.Name}");
        return true;
    }

    private static string AuditAction(Permission permission) =>
        $"permission.{permission.ToString().ToLowerInvariant()}";

    /// <summary>
    /// PIN entry on a keypad. A counter has no keyboard, and a PIN typed on an
    /// on-screen QWERTY is a PIN read over the customer's shoulder.
    /// </summary>
    public static async Task<string?> PromptPinAsync(string title)
    {
        var owner = Owner;
        if (owner is null) return null;

        string? result = null;
        var entry = new System.Text.StringBuilder();

        var display = new TextBlock
        {
            FontSize = 30,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            MinHeight = 40,
            Foreground = Fg,
        };

        var dlg = new Window
        {
            Title = title,
            Width = 360,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Bg,
        };

        void Redraw() => display.Text = new string('●', entry.Length);

        var pad = new UniformGrid { Columns = 3 };
        foreach (var key in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "⌫", "0", "✓" })
        {
            var button = new Button { Content = key };
            button.Classes.Add("key");
            button.Click += (_, _) =>
            {
                switch (key)
                {
                    case "⌫":
                        if (entry.Length > 0) entry.Length--;
                        Redraw();
                        break;
                    case "✓":
                        result = entry.ToString();
                        dlg.Close();
                        break;
                    default:
                        if (entry.Length < 8) entry.Append(key);
                        Redraw();
                        break;
                }
            };
            pad.Children.Add(button);
        }

        var cancel = new Button { Content = UiText.Cancel, Height = 52 };
        cancel.Classes.Add("btn");
        cancel.Click += (_, _) => dlg.Close();

        dlg.Content = new Border
        {
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 17,
                        FontWeight = FontWeight.Bold,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Fg,
                    },
                    display,
                    pad,
                    cancel,
                },
            },
        };

        await dlg.ShowDialog(owner);
        return result;
    }
}
