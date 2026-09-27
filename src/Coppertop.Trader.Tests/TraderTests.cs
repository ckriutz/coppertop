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

    [Theory]
    [InlineData(101.0, "take_profit")]
    [InlineData(97.9, "stop_loss")]
    [InlineData(100.0, null)]
    public void ExitReason_ByBid(double bid, string? expected)
    {
        var pos = new PositionDto(1, 1, "XBTUSD", 0.1m, 99.5m, 0.02m, 9.97m, 101.0m, 97.9m, "open", true);
        var b = (decimal)bid;
        Assert.Equal(expected, DipStrategy.ExitReason(pos, new Ticker("XBTUSD", b + 0.1m, b, b)));
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

    [Theory]
    [InlineData(0.123456789, 8, "0.12345678")]
    [InlineData(97.19, 1, "97.1")]
    [InlineData(5, 2, "5.00")]
    public void Format_TruncatesToPairDecimals(double value, int decimals, string expected) =>
        Assert.Equal(expected, KrakenClient.Format((decimal)value, decimals));
}
