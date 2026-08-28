using System.Text.Json;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Vendor;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The fundamentals response the fixture had no case for, and the number the vendor writes as a word.
///
/// The golden fixture held thirty captured <c>fundamentals</c> responses before 3.8 and every one of
/// them was a stock that resolves, so the parse was exercised thirty times against nothing that
/// could go wrong. On 2026-08-27 the sector walk died on `MUZ`, whose response the vendor delivered
/// with a status of 200 and a capitalisation of the string <c>"NA"</c>. That response is now
/// <c>fixtures/captured/fundamentals-MUZ.json</c>, captured verbatim, and it is the subject here.
///
/// The response is served over the transport rather than the interface, so what runs is the real
/// client over the real bytes: the field names, the number handling and the shape are all the
/// vendor's own. A test that handed the stage an object some author built would have passed on the
/// day the walk died.
/// </summary>
public sealed class FundamentalsParseTests
{
    /// <summary>A budget that counts and never refuses, so a parse test is not also a budget test.</summary>
    private sealed class Unlimited : ICallBudget
    {
        public int CallsUsed { get; private set; }

        public int CallsRemaining => int.MaxValue;

        public bool TryCountCall() => TryCountCalls(1);

        public bool TryCountCalls(int cost)
        {
            CallsUsed += cost;
            return true;
        }

        public void CountCall() => TryCountCall();
    }

    /// <summary>
    /// The captured failure, read end to end. Before 3.8 this threw a JsonException out of the
    /// client, up through the stage, and out of the process.
    /// </summary>
    [Fact]
    public async Task The_captured_response_that_killed_the_sector_walk_now_reads_as_a_name_the_vendor_has_nothing_on()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);

        VendorResult<VendorFundamentals?> answer =
            await replay.Vendor.GetFundamentalsAsync("MUZ", new Unlimited());

        // Delivered, and delivering nothing. Those are different facts and the stage reads them
        // apart: a request the budget refused leaves the name unasked, and a request the vendor
        // answered with nothing stamps it so it is never asked again.
        Assert.False(answer.BudgetExhausted);

        // Nothing, rather than a row of nulls. A row of nulls would be counted as resolved and
        // would say the vendor knows this name has no sector, which is the opposite of the truth.
        Assert.Null(answer.Value);
    }

    /// <summary>And an ordinary name still reads exactly as it did, over the same path.</summary>
    [Fact]
    public async Task An_ordinary_captured_response_still_reads_its_three_fields()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);

        VendorFundamentals? found = (await replay.Vendor.GetFundamentalsAsync("IESC", new Unlimited())).Require();

        Assert.NotNull(found);
        Assert.Equal("Industrials", found.Sector);
        Assert.Equal("Engineering & Construction", found.Industry);
        Assert.Equal(12_481_812_480m, found.MarketCap);
    }

    /// <summary>
    /// What the converter reads as absent, and what it still refuses.
    ///
    /// The refusing half is the one worth keeping. A converter that swallowed every unparseable
    /// string would turn a genuine change in the vendor's shape into a column of quiet nulls, which
    /// is the same failure one layer along: the field names were pinned at 1.3 because the
    /// convention-named version deserialized to a row of nulls without erroring.
    /// </summary>
    [Theory]
    [InlineData("null", null)]
    [InlineData("12481812480", 12481812480d)]
    [InlineData("\"12481812480\"", 12481812480d)]
    [InlineData("\"NA\"", null)]
    [InlineData("\"na\"", null)]
    [InlineData("\"N/A\"", null)]
    [InlineData("\"\"", null)]
    [InlineData("\"   \"", null)]
    [InlineData("\"None\"", null)]
    public void The_converter_reads_the_vendors_own_words_for_a_figure_it_does_not_hold(string json, double? expected)
    {
        decimal? read = JsonSerializer.Deserialize<decimal?>(json, Options());

        Assert.Equal(expected is null ? null : (decimal)expected.Value, read);
    }

    [Theory]
    [InlineData("\"about ten billion\"")]
    [InlineData("\"1.2.3\"")]
    [InlineData("true")]
    public void A_string_that_is_neither_a_number_nor_an_absence_word_still_throws(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<decimal?>(json, Options()));
    }

    private static JsonSerializerOptions Options() =>
        new(JsonSerializerDefaults.Web) { Converters = { new TolerantDecimalConverter() } };
}
