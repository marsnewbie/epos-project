using RingOrder.Epos.Hardware;
using Xunit;

namespace RingOrder.Epos.Tests;

/// <summary>
/// The two protocols the till speaks to hardware it does not have yet. Both are
/// tested against what the devices actually send rather than against what the
/// datasheets say, because there is no single standard for either.
/// </summary>
public class CallerIdDecoderTests
{
    private static CallerIdCall? Feed(params string[] lines)
    {
        var decoder = new CallerIdDecoder();
        CallerIdCall? last = null;
        foreach (var line in lines)
            last = decoder.Accept(line) ?? last;
        return last ?? decoder.Flush();
    }

    [Fact]
    public void Reads_the_multi_line_format_a_uk_line_delivers()
    {
        var call = Feed("RING", "DATE = 0217", "TIME = 1234", "NMBR = 07700900123", "NAME = J SMITH");

        Assert.NotNull(call);
        Assert.Equal("07700900123", call!.Number);
        Assert.Equal("J SMITH", call.Name);
        Assert.Equal(CallerIdWithheld.None, call.Withheld);
    }

    [Fact]
    public void Reads_the_single_line_format_a_cheap_usb_box_sends()
    {
        var call = Feed("RING", "DATE=0217TIME=1234NMBR=07700900123NAME=J SMITH");

        Assert.NotNull(call);
        Assert.Equal("07700900123", call!.Number);
        Assert.Equal("J SMITH", call.Name);
    }

    /// <summary>
    /// The trap worth a test of its own: the network sends the letter P for a
    /// withheld number. Stored as a number it puts a customer called "P" in the
    /// phone book and searches for them on every withheld call after.
    /// </summary>
    [Theory]
    [InlineData("P")]
    [InlineData("PRIVATE")]
    [InlineData("WITHHELD")]
    public void A_withheld_number_is_not_a_number(string marker)
    {
        var call = Feed("RING", $"NMBR = {marker}", "NAME = P");

        Assert.NotNull(call);
        Assert.False(call!.HasNumber);
        Assert.Equal(CallerIdWithheld.Private, call.Withheld);
        Assert.Null(call.Name);
    }

    [Theory]
    [InlineData("O")]
    [InlineData("OUT OF AREA")]
    [InlineData("UNAVAILABLE")]
    public void An_unavailable_number_is_told_apart_from_a_withheld_one(string marker)
    {
        var call = Feed("RING", $"NMBR = {marker}");

        Assert.NotNull(call);
        Assert.False(call!.HasNumber);

        // Different things to say on screen: one is the caller's choice, the
        // other is the network being unable to help.
        Assert.Equal(CallerIdWithheld.Unavailable, call.Withheld);
    }

    [Fact]
    public void A_number_with_punctuation_comes_back_as_digits()
    {
        var call = Feed("RING", "NMBR = 0121 456 7890");
        Assert.Equal("01214567890", call!.Number);
    }

    [Fact]
    public void Two_calls_in_one_stream_do_not_merge()
    {
        var decoder = new CallerIdDecoder();
        var calls = new List<CallerIdCall>();

        foreach (var line in new[]
                 {
                     "RING", "NMBR = 07700900111",
                     "RING", "NMBR = 07700900222",
                 })
        {
            if (decoder.Accept(line) is { } call) calls.Add(call);
        }

        // What the serial pump does when the line goes quiet: the last call has
        // no RING after it to close it.
        if (decoder.Flush() is { } lastCall) calls.Add(lastCall);

        Assert.Equal(2, calls.Count);
        Assert.Equal("07700900111", calls[0].Number);
        Assert.Equal("07700900222", calls[1].Number);
    }

    [Fact]
    public void Noise_between_calls_raises_nothing()
    {
        var decoder = new CallerIdDecoder();
        foreach (var line in new[] { "", "OK", "AT+VCID=1", "RING" })
            Assert.Null(decoder.Accept(line));
    }

    [Fact]
    public void A_name_containing_a_label_word_is_not_split_on_it()
    {
        // "NAME = TIMOTHY" contains "TIME"; a naive scan would cut the name in
        // half and invent a time field.
        var call = Feed("RING", "NMBR = 07700900123", "NAME = TIMOTHY DATENSHAW");
        Assert.Equal("TIMOTHY DATENSHAW", call!.Name);
    }
}

/// <summary>
/// The card terminal contract. Every test here is about the one failure that
/// matters: charging a customer twice.
/// </summary>
public class PaymentTerminalTests
{
    private static PaymentRequest Sale(string reference, decimal amount = 12.40m) =>
        new() { Reference = reference, Amount = amount, OrderNumber = "A-1001" };

    [Fact]
    public async Task An_approved_sale_carries_what_the_receipt_needs()
    {
        var terminal = new SimulatedPaymentTerminal { Latency = TimeSpan.Zero };
        var result = await terminal.StartAsync(Sale("ref-1"));

        Assert.True(result.IsApproved);
        Assert.Equal("ref-1", result.Reference);
        Assert.Equal(12.40m, result.AmountAuthorised);
        Assert.False(string.IsNullOrWhiteSpace(result.AuthCode));
    }

    [Fact]
    public async Task A_decline_authorises_nothing()
    {
        var terminal = new SimulatedPaymentTerminal { NextDeclines = true, Latency = TimeSpan.Zero };
        var result = await terminal.StartAsync(Sale("ref-2"));

        Assert.False(result.IsApproved);
        Assert.False(result.NeedsResolving);   // the terminal answered; it just said no
        Assert.Equal(0m, result.AmountAuthorised);
    }

    /// <summary>
    /// The case the whole design exists for. The terminal took the money and the
    /// till never heard. Retrying the sale would charge the customer twice, so
    /// the till asks about the reference it chose instead.
    /// </summary>
    [Fact]
    public async Task A_lost_answer_is_recovered_by_asking_about_the_reference()
    {
        var terminal = new SimulatedPaymentTerminal { NextLosesTheAnswer = true, Latency = TimeSpan.Zero };

        var sent = await terminal.StartAsync(Sale("ref-3"));
        Assert.True(sent.NeedsResolving);
        Assert.Equal(PaymentOutcome.Unknown, sent.Outcome);

        var recovered = await terminal.QueryAsync("ref-3");

        Assert.True(recovered.IsApproved);
        Assert.Equal(12.40m, recovered.AmountAuthorised);
    }

    /// <summary>
    /// The other half: a reference the terminal never saw means nobody was
    /// charged. This is the only case where the till may safely conclude the
    /// money did not move.
    /// </summary>
    [Fact]
    public async Task A_reference_the_terminal_never_saw_means_nothing_was_taken()
    {
        var terminal = new SimulatedPaymentTerminal { Latency = TimeSpan.Zero };
        var result = await terminal.QueryAsync("never-sent");

        Assert.Equal(PaymentOutcome.Failed, result.Outcome);
        Assert.False(result.IsApproved);
        Assert.False(result.NeedsResolving);
    }

    [Fact]
    public async Task A_lost_answer_that_was_declined_recovers_as_declined()
    {
        var terminal = new SimulatedPaymentTerminal { NextDeclines = true, NextLosesTheAnswer = true, Latency = TimeSpan.Zero };

        Assert.True((await terminal.StartAsync(Sale("ref-4"))).NeedsResolving);

        var recovered = await terminal.QueryAsync("ref-4");
        Assert.Equal(PaymentOutcome.Declined, recovered.Outcome);
    }

    /// <summary>
    /// The manual terminal has no way to check, and says so. Inventing an
    /// approval here would be the till asserting something it cannot know about
    /// a customer's money.
    /// </summary>
    [Fact]
    public async Task The_manual_terminal_never_pretends_to_know()
    {
        var terminal = new ManualCardTerminal();

        Assert.True((await terminal.StartAsync(Sale("ref-5"))).IsApproved);

        var queried = await terminal.QueryAsync("ref-5");
        Assert.True(queried.NeedsResolving);
    }
}
