namespace Coppertop.Trader.Kraken;

public sealed record PairInfo(
    string Key,
    string AltName,
    int PriceDecimals,
    int LotDecimals,
    decimal OrderMin,
    decimal CostMin);

public sealed record Ticker(string AltName, decimal Ask, decimal Bid, decimal Last)
{
    public decimal Mid => (Ask + Bid) / 2m;
    public decimal SpreadPct => Mid == 0 ? 0 : (Ask - Bid) / Mid * 100m;
}

public sealed record Candle(DateTimeOffset Time, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

public sealed record PublicTrade(decimal Price, decimal Volume, DateTimeOffset Time);

public sealed record TradesPage(IReadOnlyList<PublicTrade> Trades, string Last);

public sealed record AddOrderRequest(
    string Pair,
    string Side,
    string OrderType,
    decimal Price,
    decimal Volume,
    bool PostOnly,
    decimal? CloseLimitPrice,
    bool ValidateOnly);

public sealed record AddOrderResult(IReadOnlyList<string> TxIds, string Description, string? CloseDescription);

public sealed class KrakenException(string message) : Exception(message);
