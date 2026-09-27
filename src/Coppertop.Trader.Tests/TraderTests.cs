using Coppertop.Trader.Api;
using Coppertop.Trader.Kraken;
using Coppertop.Trader.Trading;

namespace Coppertop.Trader.Tests;

public class FeeMathTests
{
    [Fact]
    public void RequiredTakeProfit_CoversBothMakerLegsPlusMinProfit() =>
        Assert.Equal(0.80m, FeeMath.RequiredTakeProfitPct(0.25m, 0.30m));

    [Fact]
    public void FewCentsOnTenDollars_IsNetNegativeAfterFees()
    {
        // $10 buy, sell +0.3% (3 cents gross) at maker 0.25% each way → loses money.
        var pnl = FeeMath.NetPnl(100m, 100.3m, 0.1m, 0.25m, 0.25m);
        Assert.True(pnl < 0, $"expected loss, got {pnl}");
    }

    [Fact]
    public void OnePointFivePercent_IsNetPositive()
    {
        var pnl = FeeMath.NetPnl(100m, 101.5m, 0.1m, 0.25m, 0.25m);
        Assert.Equal(0.0996250m, pnl);
    }
}

public class IndicatorTests
{
    [Fact]
    public void SmaAndStdDev_UseLastPeriodValues()
    {
        var (sma, sd) = Indicators.SmaAndStdDev([1000m, 2m, 4m, 4m, 4m, 5m, 5m, 7m, 9m], 8);
        Assert.Equal(5m, sma);
        Assert.Equal(2m, sd, 6);
    }

    [Fact]
    public void Throws_WhenNotEnoughData() =>
        Assert.Throws<ArgumentException>(() => Indicators.SmaAndStdDev([1m, 2m], 5));
}

public class DipStrategyTests
{
    private static readonly TraderOptions Options = new() { SmaPeriod = 5, BandStdDevs = 1m };
    private static readonly PairInfo Pair = new("XXBTZUSD", "XBTUSD", 1, 8, 0.00005m, 0.5m);
    private static readonly PortfolioState Empty = new(100m, 0, new Dictionary<string, decimal>());

    // SMA 100, population σ ≈ 1.789 → dip band ≈ 98.21 (k = 1).
    private static readonly decimal[] Closes = [98m, 102m, 98m, 102m, 100m];

    private static OpportunityDto Opp(decimal tp = 1.5m, decimal maxEntry = 1000m, string strategy = "dip") =>
        new(1, "XBTUSD", strategy, maxEntry, tp, 2.0m, 12m, 0.7m, "test", DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public void Enters_WhenAskBelowBand()
    {
        var d = DipStrategy.Evaluate(Opp(), new Ticker("XBTUSD", 97.1m, 97.0m, 97.0m), Closes, Pair, Empty, Options);

        Assert.NotNull(d.Plan);
        var p = d.Plan!;
        Assert.Equal(97.0m, p.EntryPrice);
        Assert.Equal(98.5m, p.TakeProfitPrice);   // 97 * 1.015 = 98.455 → rounded up to 1 dp
        Assert.Equal(95.0m, p.StopLossPrice);     // 97 * 0.98 = 95.06 → rounded down
        Assert.True(p.SpendUsd <= 12m);
        Assert.True(p.SpendUsd * 1.0025m <= 12m, "entry fee must fit inside the spend cap");
    }

    [Fact]
    public void Skips_WhenAskAboveBand() =>
        Assert.Null(DipStrategy.Evaluate(Opp(), new Ticker("XBTUSD", 99.0m, 98.95m, 99m), Closes, Pair, Empty, Options).Plan);

    [Fact]
    public void Skips_WhenTakeProfitDoesNotCoverFees()
    {
        var d = DipStrategy.Evaluate(Opp(tp: 0.5m), new Ticker("XBTUSD", 97.1m, 97.0m, 97m), Closes, Pair, Empty, Options);
        Assert.Null(d.Plan);
        Assert.Contains("fee-adjusted", d.Reason);
    }

    [Fact]
    public void Skips_WhenAboveResearchMaxEntry() =>
        Assert.Null(DipStrategy.Evaluate(Opp(maxEntry: 96m), new Ticker("XBTUSD", 97.1m, 97.0m, 97m), Closes, Pair, Empty, Options).Plan);

    [Fact]
    public void Skips_WhenSpreadTooWide() =>
        Assert.Null(DipStrategy.Evaluate(Opp(), new Ticker("XBTUSD", 97.5m, 97.0m, 97m), Closes, Pair, Empty, Options).Plan);

    [Fact]
    public void Skips_WhenCashBelowMinimumTrade()
    {
        var poor = Empty with { AvailableCashUsd = 6m };
        var d = DipStrategy.Evaluate(Opp(), new Ticker("XBTUSD", 97.1m, 97.0m, 97m), Closes, Pair, poor, Options);
        Assert.Null(d.Plan);
        Assert.Contains("below minimum", d.Reason);
    }

    [Fact]
    public void Skips_WhenMaxOpenPositionsReached()
    {
        var full = Empty with { OpenPositions = Options.MaxOpenPositions };
        Assert.Null(DipStrategy.Evaluate(Opp(), new Ticker("XBTUSD", 97.1m, 97.0m, 97m), Closes, Pair, full, Options).Plan);
    }

    [Fact]
    public void Skips_UnknownStrategy() =>
        Assert.Null(DipStrategy.Evaluate(Opp(strategy: "grid"), new Ticker("XBTUSD", 97.1m, 97.0m, 97m), Closes, Pair, Empty, Options).Plan);
}

public class PaperFillTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly TraderOptions Options = new();

    private static OrderDto Order(decimal price = 100m) =>
        new(1, 1, null, "XBTUSD", "buy", "limit", "entry", price, 0.1m, "open", true, null, T0);

    private static PositionDto Position() =>
        new(1, 1, "XBTUSD", 0.1m, 100m, 0.025m, 10.025m, 101.5m, 98.0m, "open", true, T0);

    private static PublicTrade Print(decimal price, int secondsAfterT0) => new(price, 0.01m, T0.AddSeconds(secondsAfterT0));

    private static Ticker Quote(decimal bid, decimal? ask = null) => new("XBTUSD", ask ?? bid + 0.01m, bid, bid);

    [Fact]
    public void Entry_PrintAtLimit_DoesNotFill() =>
        Assert.Null(PaperFills.Entry(Order(), [Print(100m, 5)], Quote(100.1m), T0.AddSeconds(30)));

    [Fact]
    public void Entry_PrintBelowLimit_BeforePlacement_DoesNotFill() =>
        Assert.Null(PaperFills.Entry(Order(), [Print(99m, -5)], Quote(100.1m), T0.AddSeconds(30)));

    [Fact]
    public void Entry_PrintBelowLimit_FillsAtLimit_AtFirstThroughTime()
    {
        var fill = PaperFills.Entry(Order(), [Print(100m, 3), Print(99.9m, 7), Print(99.8m, 9)], Quote(100.1m), T0.AddSeconds(30));
        Assert.NotNull(fill);
        Assert.Equal(100m, fill!.Price);
        Assert.Equal(T0.AddSeconds(7), fill.ExecutedAt);
    }

    [Fact]
    public void Entry_AskBelowLimit_Fills() =>
        Assert.NotNull(PaperFills.Entry(Order(), [], Quote(99.8m, 99.9m), T0.AddSeconds(30)));

    [Fact]
    public void Cancel_WaitsWhileFresh() =>
        Assert.Null(PaperFills.EntryCancelReason(Order(), Quote(100.1m), false, T0.AddMinutes(5), Options));

    [Fact]
    public void Cancel_AfterTimeout() =>
        Assert.Contains("not filled", PaperFills.EntryCancelReason(Order(), Quote(100.1m), false, T0.AddMinutes(15), Options));

    [Fact]
    public void Cancel_WhenVetoed() =>
        Assert.Equal("asset vetoed", PaperFills.EntryCancelReason(Order(), Quote(100.1m), true, T0.AddMinutes(1), Options));

    [Fact]
    public void Cancel_WhenPriceRunsAway() =>
        Assert.Contains("ran away", PaperFills.EntryCancelReason(Order(), Quote(100.6m), false, T0.AddMinutes(1), Options));

    [Fact]
    public void Exit_PrintAtTakeProfit_DoesNotFill() =>
        Assert.Null(PaperFills.Exit(Position(), [Print(101.5m, 60)], Quote(101.4m), T0.AddMinutes(2), 0.1m, 1));

    [Fact]
    public void Exit_PrintAboveTakeProfit_FillsAtTakeProfit()
    {
        var fill = PaperFills.Exit(Position(), [Print(101.6m, 60)], Quote(101.2m), T0.AddMinutes(2), 0.1m, 1);
        Assert.Equal("take_profit", fill!.Reason);
        Assert.Equal(101.5m, fill.Price);
        Assert.Equal(T0.AddSeconds(60), fill.ExecutedAt);
    }

    [Fact]
    public void Exit_IgnoresPrintsBeforeEntryFill() =>
        Assert.Null(PaperFills.Exit(Position(), [Print(102m, -10), Print(97m, -5)], Quote(100m), T0.AddMinutes(2), 0.1m, 1));

    [Fact]
    public void Exit_StopWick_FillsAtStopLessSlippage_EvenIfBidRecovered()
    {
        var fill = PaperFills.Exit(Position(), [Print(97.9m, 60)], Quote(99m), T0.AddMinutes(2), 0.1m, 1);
        Assert.Equal("stop_loss", fill!.Reason);
        Assert.Equal(97.9m, fill.Price); // 98.0 * 0.999 = 97.902 → rounded down to 1 dp
    }

    [Fact]
    public void Exit_GapBelowStop_FillsAtBidLessSlippage()
    {
        var fill = PaperFills.Exit(Position(), [], Quote(95m), T0.AddMinutes(2), 0.1m, 1);
        Assert.Equal("stop_loss", fill!.Reason);
        Assert.Equal(94.9m, fill.Price); // 95 * 0.999 = 94.905
    }

    [Fact]
    public void Exit_WhicheverHappenedFirstWins()
    {
        var tpFirst = PaperFills.Exit(Position(), [Print(101.6m, 10), Print(97.5m, 20)], Quote(99m), T0.AddMinutes(1), 0.1m, 1);
        Assert.Equal("take_profit", tpFirst!.Reason);

        var stopFirst = PaperFills.Exit(Position(), [Print(97.5m, 10), Print(101.6m, 20)], Quote(99m), T0.AddMinutes(1), 0.1m, 1);
        Assert.Equal("stop_loss", stopFirst!.Reason);
    }

    [Fact]
    public void ExitPrices_RoundInTheConservativeDirection()
    {
        var (tp, sl) = DipStrategy.ExitPrices(97m, 1.5m, 2m, 1);
        Assert.Equal(98.5m, tp);
        Assert.Equal(95.0m, sl);
    }
}

public class KrakenTests
{
    [Fact]
    public void Signer_MatchesKrakenDocumentationExample()
    {
        const string secret = "kQH5HW/8p1uGOVjbgWA7FunAmGO8lsSUXNsu3eow76sz84Q18fWxnyRzBHCd3pd5nE9qa99HAZtuZuj6F1huXg==";
        const string nonce = "1616492376594";
        const string postData = "nonce=1616492376594&ordertype=limit&pair=XBTUSD&price=37500&type=buy&volume=1.25";

        var sig = KrakenSigner.Sign("/0/private/AddOrder", nonce, postData, secret);

        Assert.Equal("4/dpxb3iT4tp/ZCVEwSnEsLxx0bqyhLpdfOpc6fn7OR8+UClSV5n9E6aSS8MPtnRfp32bAb0nmbRn6H8ndwLUQ==", sig);
    }

    [Fact]
    public void ParseTrades_ReadsRowsAndCursor()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"XXBTZUSD":[["65000.10000","0.00100000",1700000000.1234,"s","m","",1],
                         ["64999.90000","0.00200000",1700000001.5,"b","l","",2]],
             "last":"1700000001500000000"}
            """);
        var page = KrakenClient.ParseTrades(doc.RootElement, "0");

        Assert.Equal(2, page.Trades.Count);
        Assert.Equal(64999.9m, page.Trades[1].Price);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000123), page.Trades[0].Time);
        Assert.Equal("1700000001500000000", page.Last);
    }

    [Theory]
    [InlineData(0.123456789, 8, "0.12345678")]
    [InlineData(97.19, 1, "97.1")]
    [InlineData(5, 2, "5.00")]
    public void Format_TruncatesToPairDecimals(double value, int decimals, string expected) =>
        Assert.Equal(expected, KrakenClient.Format((decimal)value, decimals));
}
