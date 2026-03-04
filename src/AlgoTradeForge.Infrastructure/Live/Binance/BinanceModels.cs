using System.Text.Json.Serialization;

namespace AlgoTradeForge.Infrastructure.Live.Binance;

public sealed record BinanceNewOrderResponse
{
    [JsonPropertyName("orderId")]
    public long OrderId { get; init; }

    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("side")]
    public string Side { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("executedQty")]
    public string ExecutedQty { get; init; } = "0";

    [JsonPropertyName("cummulativeQuoteQty")]
    public string CummulativeQuoteQty { get; init; } = "0";

    [JsonPropertyName("fills")]
    public List<BinanceOrderFill> Fills { get; init; } = [];
}

public sealed record BinanceOrderFill
{
    [JsonPropertyName("price")]
    public string Price { get; init; } = "0";

    [JsonPropertyName("qty")]
    public string Qty { get; init; } = "0";

    [JsonPropertyName("commission")]
    public string Commission { get; init; } = "0";

    [JsonPropertyName("commissionAsset")]
    public string CommissionAsset { get; init; } = "";
}

public sealed record BinanceAccountInfo
{
    [JsonPropertyName("balances")]
    public List<BinanceBalance> Balances { get; init; } = [];
}

public sealed record BinanceBalance
{
    [JsonPropertyName("asset")]
    public string Asset { get; init; } = "";

    [JsonPropertyName("free")]
    public string Free { get; init; } = "0";

    [JsonPropertyName("locked")]
    public string Locked { get; init; } = "0";
}
