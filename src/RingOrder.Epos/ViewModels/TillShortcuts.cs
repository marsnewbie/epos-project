using Avalonia.Controls;
using Avalonia.Input;

namespace RingOrder.Epos.ViewModels;

/// <summary>What a keypress on the till screen should do.</summary>
public enum TillShortcut
{
    None,

    /// <summary>Append <see cref="TillKeyResult.Digit"/> to the dish-number entry.</summary>
    AppendDigit,

    /// <summary>The quantity separator, so 3*88 is three of dish 88.</summary>
    AppendTimes,

    /// <summary>Rub out the last character typed.</summary>
    Backspace,

    /// <summary>Add what has been keyed.</summary>
    Commit,

    /// <summary>Abandon what has been keyed, or close the options panel.</summary>
    Cancel,

    QuantityUp,
    QuantityDown,
}

public readonly record struct TillKeyResult(TillShortcut Action, char Digit = '\0');

/// <summary>
/// The keyboard on a till that has one.
/// <para>
/// Experienced staff work by dish number and barely look at the tiles — the
/// screen has taken `88` and `3x88` since the interface work, but only from an
/// on-screen box. A shop with a numeric keypad should not have to reach for the
/// glass to use it.
/// </para>
/// <para>
/// Pure, and separate from the view, because the rule that matters is a
/// judgement about focus rather than about keys: see
/// <see cref="Resolve"/>.
/// </para>
/// </summary>
public static class TillShortcuts
{
    /// <summary>
    /// Decides what a key means.
    /// <para>
    /// <paramref name="typingIntoAField"/> is the whole safety of this feature.
    /// A cashier entering a house number, a phone number or a note is typing
    /// digits, and a shortcut layer that swallowed them would put the customer's
    /// address into the dish-number box and silently drop it from the ticket.
    /// While any text field has focus, the keyboard belongs to that field and
    /// nothing here fires.
    /// </para>
    /// </summary>
    public static TillKeyResult Resolve(Key key, bool typingIntoAField)
    {
        if (typingIntoAField) return new TillKeyResult(TillShortcut.None);

        // Both rows: the number strip above the letters and the keypad on the
        // right. A till with a keypad is the case this exists for, but the same
        // keys on a laptop must not behave differently.
        if (key is >= Key.D0 and <= Key.D9)
            return new TillKeyResult(TillShortcut.AppendDigit, (char)('0' + (key - Key.D0)));

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return new TillKeyResult(TillShortcut.AppendDigit, (char)('0' + (key - Key.NumPad0)));

        return key switch
        {
            Key.Multiply => new TillKeyResult(TillShortcut.AppendTimes),
            Key.Enter => new TillKeyResult(TillShortcut.Commit),
            Key.Escape => new TillKeyResult(TillShortcut.Cancel),
            Key.Back => new TillKeyResult(TillShortcut.Backspace),

            // The two keys either side of Enter on a keypad, which is where a
            // hand already is after keying a dish number.
            Key.Add or Key.OemPlus => new TillKeyResult(TillShortcut.QuantityUp),
            Key.Subtract or Key.OemMinus => new TillKeyResult(TillShortcut.QuantityDown),

            _ => new TillKeyResult(TillShortcut.None),
        };
    }

    /// <summary>
    /// Whether the keyboard currently belongs to something the staff member is
    /// typing into.
    /// <para>
    /// Asked of the focused control rather than tracked as state, because state
    /// gets out of step with focus exactly once and then the address field eats
    /// nothing for the rest of the shift.
    /// </para>
    /// </summary>
    public static bool IsTextEntry(object? focused) => focused is TextBox or AutoCompleteBox;
}
