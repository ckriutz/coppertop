using Coppertop.Research;
using Coppertop.Research.Kraken;
using Coppertop.Research.Screening;

namespace Coppertop.Research.Tests;

internal static class Candles
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Candle At(int i, decimal close, decimal? low = null, decimal? high = null, decimal? open = null, int minutes = 5) =>
        new(T0.AddMinutes(i * minutes), open ?? close, high ?? close, low ?? close, close, 1m);

    /// <summary>A flat series at 100 with alternating 99.9/100.1 so σ is non-zero.</summary>
    public static List<Candle> Flat(int count, int minutes = 5) =>
        Enumerable.Range(0, count).Select(i => At(i, i % 2 == 0 ? 99.9m : 100.1m, minutes: minutes)).ToList();

    /// <summary>
    /// 15m series of repeated "flat, dip to 99, then outcome" blocks. A win spikes to 103.5 (clears every TP in the
    /// grid); a loss drops to 95 (hits every SL). Ends with a still-forming candle.
    /// </summary>
    public static List<Candle> Dips(params bool[] wins)
    {
        var c = new List<Candle>();
        void Flat(int n) { for (var k = 0; k < n; k++) c.Add(At(c.Count, c.Count % 2 == 0 ? 99.9m : 100.1m, minutes: 15)); }
        foreach (var win in wins)
        {
            Flat(22);
            c.Add(At(c.Count, 99m, low: 99m, minutes: 15));
            c.Add(win
                ? At(c.Count, 100.1m, high: 103.5m, minutes: 15)
                : At(c.Count, 99.9m, low: 95m, minutes: 15));
        }
        Flat(6);
        return c;
    }
}

public class IndicatorTests
{
    [Fact]
    public void Rsi_AllGains_Is100_AllLosses_Is0()
    {
        Assert.Equal(100m, Indicators.Rsi(Enumerable.Range(1, 30).Select(i => (decimal)i).ToList()));
        Assert.Equal(0m, Indicators.Rsi(Enumerable.Range(1, 30).Select(i => (decimal)(100 - i)).ToList()));
    }

    [Fact]
    public void WilsonLower_ShrinksSmallSamples()
    {
        Assert.Equal(0m, Indicators.WilsonLower(0, 0));
        Assert.True(Indicators.WilsonLower(3, 3) < 0.5m);         // 3/3 isn't proof
        Assert.True(Indicators.WilsonLower(90, 100) > 0.8m);
    }

    [Fact]
    public void AtrPct_UsesTrueRange()
    {
        var c = new List<Candle> { Candles.At(0, 100m) };
        for (var i = 1; i <= 14; i++) c.Add(Candles.At(i, 100m, low: 99m, high: 101m, minutes: 60));
        Assert.Equal(2m, Indicators.AtrPct(c));
    }
}

public class DipSimulatorTests
{
    private static readonly DipSimSettings S = new(20, 1.5m, 1.5m, 2.0m, 0.25m, 0.40m, 0.10m);

    [Fact]
    public void DipThenRally_IsAWin()
    {
        var c = Candles.Flat(20);
        c.Add(Candles.At(20, 99m, low: 99m));                // dip below band (~99.85)
        c.Add(Candles.At(21, 101.5m, high: 101.6m));         // > entry × 1.015
        var r = DipSimulator.Run(c, S);

        Assert.Equal(1, r.Wins);
        Assert.Equal(0, r.Losses);
        Assert.True(r.AvgNetPct is > 0.9m and < 1.1m);       // 1.5% − two maker fees
    }

    [Fact]
    public void CandleTouchingTpAndSl_CountsAsLoss()
    {
        var c = Candles.Flat(20);
        c.Add(Candles.At(20, 99m, low: 99m));
        c.Add(Candles.At(21, 100m, low: 90m, high: 110m));
        var r = DipSimulator.Run(c, S);

        Assert.Equal(0, r.Wins);
        Assert.Equal(1, r.Losses);
        Assert.True(r.AvgNetPct < -2m);
    }

    [Fact]
    public void ExitsAreNotCheckedInTheEntryCandle_AndOpenTradesAreExcluded()
    {
        var c = Candles.Flat(20);
        c.Add(Candles.At(20, 99m, low: 50m, high: 200m));   // wild entry candle: ignored for exits
        c.Add(Candles.At(21, 99.5m));
        var r = DipSimulator.Run(c, S);

        Assert.Equal(1, r.Open);
        Assert.Equal(0, r.Resolved);
        Assert.Null(r.WinRatePct);
    }

    [Fact]
    public void TimeStop_SellsAtTheCloseAfterMaxHoldCandles()
    {
        var c = Candles.Flat(20);
        c.Add(Candles.At(20, 99m, low: 99m));
        for (var i = 21; i < 30; i++) c.Add(Candles.At(i, 99.5m));
        var r = DipSimulator.Run(c, S with { MaxHoldCandles = 3 });

        Assert.Equal(1, r.TimeStops);
        Assert.Equal(1, r.Resolved);
        Assert.Equal(0, r.Wins);
        Assert.Equal(20m, r.AvgHoldMinutes);                  // entry candle + 3 more, 5 min each
        Assert.True(r.AvgNetPct is > -0.3m and < -0.2m, $"net {r.AvgNetPct}");  // 99 → 99.5 (+0.5%) less ~0.75% fees and slippage

        Assert.Equal(1, DipSimulator.Run(c, S).Open);          // no time stop: still open at the end
    }

    [Fact]
    public void Range_LimitsEntriesAndExits_SoTrainNeverSeesTest()
    {
        var c = Candles.Flat(20);
        c.Add(Candles.At(20, 99m, low: 99m));
        c.Add(Candles.At(21, 101.5m, high: 101.6m));
        Assert.Equal(1, DipSimulator.Run(c, S, 0, 21).Open);   // the winning candle is outside the range
        Assert.Equal(0, DipSimulator.Run(c, S, 21).Trades);    // no entry at or after 21
    }

    [Fact]
    public void BreakevenWinRate_ReflectsFeesAndAsymmetry()
    {
        var be = DipSimulator.BreakevenWinRatePct(S);
        Assert.True(be is > 72m and < 75m, $"breakeven {be}");  // nets +0.99% vs −2.73% after fees ≈ 73%
    }
}

public class ScreenerTests
{
    private static ResearchOptions Options() => new() { MinSimTrades = 1, MinTestTrades = 1 };

    private static Ticker Tick(decimal spreadPct = 0.02m, decimal volume = 100_000m) =>
        new("XBTUSD", 100m, 100m * (1 - spreadPct / 100m), 100m, volume, 100m);

    // Four winning dips in training, two in the unseen test part.
    private static List<Candle> WinningFast() => Candles.Dips(true, true, true, true, true, true);

    private static List<Candle> Hourly(Func<int, decimal> price, int count = 250) =>
        Enumerable.Range(0, count).Select(i => Candles.At(i, price(i), minutes: 60)).ToList();

    [Fact]
    public void ApprovesLiquidAsset_WhoseTunedSettingAlsoWonOnUnseenData()
    {
        var r = Screener.Screen("XBTUSD", Tick(), WinningFast(), Hourly(_ => 100m), Options());

        Assert.True(r.Approved, r.Reason);
        var t = r.Metrics!.Tuned!;
        Assert.Equal(3.0m, t.TakeProfitPct);                  // every dip spiked 3.5%, so the biggest TP wins
        Assert.Equal(15, t.CandleIntervalMinutes);
        Assert.Equal(4, t.Train.Wins);
        Assert.Equal(2, t.Test.Wins);
        Assert.Equal(6, r.Metrics.Sim!.Wins);                 // Sim = chosen setting over the whole period
        Assert.Equal("flat", r.Metrics.Trend);
        Assert.Contains("test", r.Reason);
    }

    [Fact]
    public void Tuner_SkipsTakeProfitsThatDontClearFees()
    {
        var o = new ResearchOptions { TuneTakeProfitPcts = [0.5m, 1.0m], MinNetProfitPct = 0.30m };
        Assert.All(Tuner.Grid(o, 15), g => Assert.Equal(1.0m, g.Settings.TakeProfitPct));
        Assert.Equal(16, Tuner.Grid(o, 15).First(g => g.HoldHours == 4).Settings.MaxHoldCandles);
    }

    [Fact]
    public void RejectsWideSpread_LowVolume_Downtrend_AndCollectsAllReasons()
    {
        var down = Hourly(i => 400m - i);  // falling; ticker last (100) < SMA50 < SMA200
        var r = Screener.Screen("XBTUSD", Tick(spreadPct: 1m, volume: 10m), WinningFast(), down, Options());

        Assert.False(r.Approved);
        Assert.Equal("down", r.Metrics!.Trend);
        Assert.Contains("spread", r.Reason);
        Assert.Contains("24h volume", r.Reason);
        Assert.Contains("downtrend", r.Reason);
    }

    [Fact]
    public void RejectsWhenNothingWonInTraining()
    {
        var r = Screener.Screen("XBTUSD", Tick(), Candles.Dips(false, false, false, false, false, false), Hourly(_ => 100m), Options());
        Assert.False(r.Approved);
        Assert.Null(r.Metrics!.Tuned);
        Assert.Contains("no dip setting made money in training", r.Reason);
        Assert.NotNull(r.Metrics.Sim);                        // default setting still shown
    }

    [Fact]
    public void RejectsWhenTheTunedSettingLostOnUnseenData()
    {
        var r = Screener.Screen("XBTUSD", Tick(), Candles.Dips(true, true, true, true, false, false), Hourly(_ => 100m), Options());
        Assert.False(r.Approved);
        Assert.NotNull(r.Metrics!.Tuned);
        Assert.Contains("lost on unseen data", r.Reason);
    }

    [Fact]
    public void RejectsWhenTestHadTooFewDips()
    {
        var r = Screener.Screen("XBTUSD", Tick(), WinningFast(), Hourly(_ => 100m), new ResearchOptions { MinSimTrades = 1, MinTestTrades = 3 });
        Assert.False(r.Approved);
        Assert.Contains("too few dips to judge on unseen data", r.Reason);
    }

    [Fact]
    public void Rank_KeepsTopApprovals_AndDemotesTheRest()
    {
        ScreenResult Ok(string a, decimal net) => new(a, true, 0.5m, "ok",
            new ScreenMetrics(1, 0, 0, null, null, null, null, "flat", new SimMetrics(1, 1, 0, 0, 0, 100, 67, net, 10, 60)));
        var ranked = Screener.Rank([Ok("A", 0.1m), Ok("B", 0.5m), Ok("C", 0.3m), ScreenResult.Failed("D", "bad")], 2);

        Assert.Equal(["B", "C"], ranked.Where(r => r.Approved).Select(r => r.Asset));
        Assert.StartsWith("ranked below the top 2", ranked.Single(r => r.Asset == "A").Reason);
    }
}
