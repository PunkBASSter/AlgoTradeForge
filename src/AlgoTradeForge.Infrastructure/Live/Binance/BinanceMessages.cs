using System.Text.Json.Serialization;

namespace AlgoTradeForge.Infrastructure.Live.Binance;

public sealed record BinanceKlineMessage
{
    [JsonPropertyName("e")]
    public string EventType { get; init; } = "";

    [JsonPropertyName("E")]
    public long EventTime { get; init; }

    [JsonPropertyName("s")]
    public string Symbol { get; init; } = "";

    [JsonPropertyName("k")]
    public BinanceKlineData Kline { get; init; } = new();
}

public sealed record BinanceKlineData
{
    [JsonPropertyName("t")]
    public long OpenTime { get; init; }

    [JsonPropertyName("T")]
    public long CloseTime { get; init; }

    [JsonPropertyName("s")]
    public string Symbol { get; init; } = "";

    [JsonPropertyName("i")]
    public string Interval { get; init; } = "";

    [JsonPropertyName("o")]
    public string Open { get; init; } = "0";

    [JsonPropertyName("h")]
    public string High { get; init; } = "0";

    [JsonPropertyName("l")]
    public string Low { get; init; } = "0";

    [JsonPropertyName("c")]
    public string Close { get; init; } = "0";

    [JsonPropertyName("v")]
    public string Volume { get; init; } = "0";

    [JsonPropertyName("x")]
    public bool IsClosed { get; init; }
}

public sealed record BinanceExecutionReport
{
    [JsonPropertyName("e")]
    public string EventType { get; init; } = "";

    [JsonPropertyName("E")]
    public long EventTime { get; init; }

    [JsonPropertyName("s")]
    public string Symbol { get; init; } = "";

    [JsonPropertyName("S")]
    public string Side { get; init; } = "";

    [JsonPropertyName("o")]
    public string OrderType { get; init; } = "";

    [JsonPropertyName("q")]
    public string OriginalQuantity { get; init; } = "0";

    [JsonPropertyName("p")]
    public string Price { get; init; } = "0";

    [JsonPropertyName("L")]
    public string LastFilledPrice { get; init; } = "0";

    [JsonPropertyName("l")]
    public string LastFilledQty { get; init; } = "0";

    [JsonPropertyName("z")]
    public string CumulativeFilledQty { get; init; } = "0";

    [JsonPropertyName("n")]
    public string Commission { get; init; } = "0";

    [JsonPropertyName("i")]
    public long OrderId { get; init; }

    [JsonPropertyName("x")]
    public string ExecutionType { get; init; } = "";

    [JsonPropertyName("X")]
    public string OrderStatus { get; init; } = "";

    [JsonPropertyName("T")]
    public long TransactionTime { get; init; }
}
