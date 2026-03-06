using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
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
}

public sealed record CandleDto(long Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
public sealed record FillDto(long OrderId, string Timestamp, decimal Price, decimal Quantity, string Side, decimal Commission);
public sealed record PendingOrderDto(long Id, string Side, string Type, decimal Quantity, decimal? LimitPrice, decimal? StopPrice);
public sealed record AccountDto(decimal InitialCash, decimal Cash, IReadOnlyList<PositionDto> Positions);
public sealed record PositionDto(string Symbol, decimal Quantity, decimal AverageEntryPrice, decimal RealizedPnl);
public sealed record LastBarDto(string Symbol, string TimeFrame, long Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

// --- Handler ---

public sealed class GetLiveSessionDataQueryHandler(
    ILiveSessionStore sessionStore,
    ILiveSessionDataProvider dataProvider,
    IHistoryRepository historyRepository,
    ILogger<GetLiveSessionDataQueryHandler> logger) : IQueryHandler<GetLiveSessionDataQuery, LiveSessionDataDto?>
{
    public Task<LiveSessionDataDto?> HandleAsync(GetLiveSessionDataQuery query, CancellationToken ct = default)
    {
        var details = sessionStore.Get(query.SessionId);
        if (details is null)
            return Task.FromResult<LiveSessionDataDto?>(null);

        var snapshot = dataProvider.GetSnapshot(query.SessionId);
        if (snapshot is null)
            return Task.FromResult<LiveSessionDataDto?>(null);

        var asset = snapshot.PrimaryAsset;
        var tickSize = asset.TickSize;

        // Load last 30 days of historical candles for the primary subscription
        var primarySub = snapshot.Subscriptions.Count > 0 ? snapshot.Subscriptions[0] : null;
        var historicalBars = new List<Int64Bar>();
        if (primarySub is not null)
        {
            try
            {
                var to = DateOnly.FromDateTime(DateTime.UtcNow);
                var from = to.AddDays(-30);
                var series = historyRepository.Load(primarySub, from, to);
                historicalBars.AddRange(series);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load historical candles for {Asset}", primarySub.Asset.Name);
            }
        }

        // Merge: historical first, then session bars, dedup by TimestampMs
        var seen = new HashSet<long>();
        var merged = new List<CandleDto>();

        foreach (var bar in historicalBars)
        {
            if (seen.Add(bar.TimestampMs))
                merged.Add(ConvertBar(bar, tickSize));
        }

        foreach (var bar in snapshot.Bars)
        {
            if (seen.Add(bar.TimestampMs))
                merged.Add(ConvertBar(bar, tickSize));
        }

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
        };

        return Task.FromResult<LiveSessionDataDto?>(dto);
    }

    private static CandleDto ConvertBar(Int64Bar bar, decimal tickSize) =>
        new(bar.TimestampMs, bar.Open * tickSize, bar.High * tickSize, bar.Low * tickSize, bar.Close * tickSize, bar.Volume);

    private static IReadOnlyList<LastBarDto> BuildLastBars(LiveSessionSnapshot snapshot, decimal tickSize)
    {
        return snapshot.LastBarsPerSubscription
            .Select(entry => new LastBarDto(
                entry.Subscription.Asset.Name,
                entry.Subscription.TimeFrame.ToString(),
                entry.Bar.TimestampMs,
                entry.Bar.Open * tickSize,
                entry.Bar.High * tickSize,
                entry.Bar.Low * tickSize,
                entry.Bar.Close * tickSize,
                entry.Bar.Volume))
            .ToList();
    }
}
