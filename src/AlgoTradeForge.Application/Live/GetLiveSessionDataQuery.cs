using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Application.Live;

public sealed record GetLiveSessionDataQuery(Guid SessionId) : IQuery<LiveSessionDataDto?>;

// --- DTOs ---

public sealed record LiveSessionDataDto
{
    public required IReadOnlyList<CandleDto> Candles { get; init; }
    public required IReadOnlyList<FillDto> Fills { get; init; }
    public required IReadOnlyList<PendingOrderDto> PendingOrders { get; init; }
    public required AccountDto Account { get; init; }
    public required string TimeFrame { get; init; }
    public required IReadOnlyList<LastBarDto> LastBars { get; init; }
    public required IReadOnlyList<ExchangeTradeDto> ExchangeTrades { get; init; }
}

/// <summary>Time is Unix seconds (not milliseconds) — required by TradingView lightweight-charts.</summary>
public sealed record CandleDto(long Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
public sealed record FillDto(long OrderId, string Timestamp, decimal Price, decimal Quantity, string Side, decimal Commission);
public sealed record PendingOrderDto(long Id, string Side, string Type, decimal Quantity, decimal? LimitPrice, decimal? StopPrice);
public sealed record ExchangeTradeDto(long OrderId, string Timestamp, decimal Price, decimal Quantity, string Side, decimal Commission, string CommissionAsset);
public sealed record AccountDto(decimal InitialCash, decimal Cash, decimal ExchangeBalance, IReadOnlyList<PositionDto> Positions);
public sealed record PositionDto(string Symbol, decimal Quantity, decimal AverageEntryPrice, decimal RealizedPnl);
public sealed record LastBarDto(string Symbol, string TimeFrame, long Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

// --- Handler ---

public sealed class GetLiveSessionDataQueryHandler(
    ILiveSessionStore sessionStore,
    ILiveSessionDataProvider dataProvider,
    ILogger<GetLiveSessionDataQueryHandler> logger) : IQueryHandler<GetLiveSessionDataQuery, LiveSessionDataDto?>
{
    public async Task<LiveSessionDataDto?> HandleAsync(GetLiveSessionDataQuery query, CancellationToken ct = default)
    {
        var details = sessionStore.Get(query.SessionId);
        if (details is null)
            return null;

        var snapshot = await dataProvider.GetSnapshotAsync(query.SessionId, ct);
        if (snapshot is null)
            return null;

        var asset = snapshot.PrimaryAsset;
        var tickSize = asset.TickSize;

        var primarySub = snapshot.Subscriptions.Count > 0 ? snapshot.Subscriptions[0] : null;

        // Fetch a small number of recent candles from exchange REST API
        // to give context around the live session bars.
        const int BackfillLimit = 20;
        var recentBars = new List<Int64Bar>();
        if (primarySub is not null)
        {
            try
            {
                var interval = MapTimeFrameToInterval(primarySub.TimeFrame);
                recentBars.AddRange(
                    await dataProvider.GetRecentKlinesAsync(
                        query.SessionId, asset.Name, interval, tickSize, BackfillLimit, ct));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch recent klines for {Asset}", asset.Name);
            }
        }

        // Merge: historical/REST first, then session bars, dedup by TimestampMs
        var seen = new HashSet<long>();
        var merged = new List<CandleDto>();

        foreach (var bar in recentBars)
        {
            if (seen.Add(bar.TimestampMs))
                merged.Add(ConvertBar(bar, tickSize));
        }

        foreach (var bar in snapshot.Bars)
        {
            if (seen.Add(bar.TimestampMs))
                merged.Add(ConvertBar(bar, tickSize));
        }

        merged.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Convert fills
        var fills = snapshot.Fills
            .Select(f => new FillDto(
                f.OrderId,
                f.Timestamp.ToString("O"),
                f.Price * tickSize,
                f.Quantity,
                f.Side.ToString(),
                f.Commission * tickSize))
            .ToList();

        // Convert pending orders
        var pendingOrders = snapshot.PendingOrders
            .Select(o => new PendingOrderDto(
                o.Id,
                o.Side.ToString(),
                o.Type.ToString(),
                o.Quantity,
                o.LimitPrice.HasValue ? o.LimitPrice.Value * tickSize : null,
                o.StopPrice.HasValue ? o.StopPrice.Value * tickSize : null))
            .ToList();

        // Convert positions
        var positions = snapshot.Positions.Values
            .Where(p => p.Quantity != 0)
            .Select(p => new PositionDto(
                p.Asset.Name,
                p.Quantity,
                p.AverageEntryPrice * tickSize,
                p.RealizedPnl * tickSize))
            .ToList();

        var account = new AccountDto(
            snapshot.InitialCash * tickSize,
            snapshot.Cash * tickSize,
            snapshot.ExchangeBalance,
            positions);

        // Build last bar per subscription
        var lastBars = BuildLastBars(snapshot, tickSize);

        var timeFrame = primarySub?.TimeFrame.ToString() ?? "00:01:00";

        var dto = new LiveSessionDataDto
        {
            Candles = merged,
            Fills = fills,
            PendingOrders = pendingOrders,
            Account = account,
            TimeFrame = timeFrame,
            LastBars = lastBars,
            ExchangeTrades = snapshot.ExchangeTrades,
        };

        return dto;
    }

    private static CandleDto ConvertBar(Int64Bar bar, decimal tickSize) =>
        new(bar.TimestampMs / 1000, bar.Open * tickSize, bar.High * tickSize, bar.Low * tickSize, bar.Close * tickSize, bar.Volume);

    private static string MapTimeFrameToInterval(TimeSpan timeFrame) => timeFrame.TotalMinutes switch
    {
        1 => "1m", 3 => "3m", 5 => "5m", 15 => "15m", 30 => "30m",
        60 => "1h", 120 => "2h", 240 => "4h", 360 => "6h", 480 => "8h",
        720 => "12h", 1440 => "1d", 4320 => "3d", 10080 => "1w",
        _ => "1m",
    };

    private static IReadOnlyList<LastBarDto> BuildLastBars(LiveSessionSnapshot snapshot, decimal tickSize)
    {
        return snapshot.LastBarsPerSubscription
            .Select(entry => new LastBarDto(
                entry.Subscription.Asset.Name,
                entry.Subscription.TimeFrame.ToString(),
                entry.Bar.TimestampMs / 1000,
                entry.Bar.Open * tickSize,
                entry.Bar.High * tickSize,
                entry.Bar.Low * tickSize,
                entry.Bar.Close * tickSize,
                entry.Bar.Volume))
            .ToList();
    }
}
