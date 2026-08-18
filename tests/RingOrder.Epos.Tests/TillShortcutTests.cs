using Avalonia.Input;
using RingOrder.Epos.ViewModels;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// The till keyboard. Experienced staff work by dish number and barely look at
/// the tiles; a shop with a numeric keypad should not have to reach for glass.
/// </summary>
public class TillShortcutTests
{
    private static TillKeyResult Key(Key key, bool typing = false) =>
        TillShortcuts.Resolve(key, typing);

    [Theory]
    [InlineData(Avalonia.Input.Key.D8, '8')]
    [InlineData(Avalonia.Input.Key.NumPad8, '8')]
    [InlineData(Avalonia.Input.Key.D0, '0')]
    [InlineData(Avalonia.Input.Key.NumPad0, '0')]
    public void Both_number_rows_key_a_dish(Key key, char expected)
    {
        var result = Key(key);
        Assert.Equal(TillShortcut.AppendDigit, result.Action);
        Assert.Equal(expected, result.Digit);
    }

    /// <summary>
    /// The whole safety of the feature. A cashier entering a house number or a
    /// phone number is typing digits, and a shortcut layer that swallowed them
    /// would put the customer's address into the dish-number box and drop it
    /// from the ticket without a word.
    /// </summary>
    [Theory]
    [InlineData(Avalonia.Input.Key.D8)]
    [InlineData(Avalonia.Input.Key.NumPad8)]
    [InlineData(Avalonia.Input.Key.Enter)]
    [InlineData(Avalonia.Input.Key.Back)]
    [InlineData(Avalonia.Input.Key.Escape)]
    [InlineData(Avalonia.Input.Key.Add)]
    public void Nothing_fires_while_a_field_has_the_keyboard(Key key)
    {
        Assert.Equal(TillShortcut.None, Key(key, typing: true).Action);
    }

    [Fact]
    public void A_text_box_counts_as_typing_and_a_button_does_not()
    {
        Assert.True(TillShortcuts.IsTextEntry(new Avalonia.Controls.TextBox()));
        Assert.False(TillShortcuts.IsTextEntry(new Avalonia.Controls.Button()));
        Assert.False(TillShortcuts.IsTextEntry(null));
    }

    [Theory]
    [InlineData(Avalonia.Input.Key.Multiply, TillShortcut.AppendTimes)]
    [InlineData(Avalonia.Input.Key.Enter, TillShortcut.Commit)]
    [InlineData(Avalonia.Input.Key.Escape, TillShortcut.Cancel)]
    [InlineData(Avalonia.Input.Key.Back, TillShortcut.Backspace)]
    [InlineData(Avalonia.Input.Key.Add, TillShortcut.QuantityUp)]
    [InlineData(Avalonia.Input.Key.Subtract, TillShortcut.QuantityDown)]
    public void The_keypad_keys_map_to_what_is_beside_them(Key key, TillShortcut expected)
    {
        Assert.Equal(expected, Key(key).Action);
    }

    [Fact]
    public void A_letter_means_nothing_here()
    {
        Assert.Equal(TillShortcut.None, Key(Avalonia.Input.Key.J).Action);
        Assert.Equal(TillShortcut.None, Key(Avalonia.Input.Key.F5).Action);
    }
}
