using System.Globalization;
using System.Text.Json;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.Events;

public sealed class JsonlRunTradeLogReader(
    IFileStorage storage,
    ILogger<JsonlRunTradeLogReader> logger) : IRunTradeLogReader
{
    public async Task<IReadOnlyList<RunTradeRecord>> Read(string runFolderPath, CancellationToken ct = default)
    {
        var eventsKey = Path.Combine(runFolderPath, "events.jsonl");
        if (!await storage.Exists(eventsKey, ct))
            return [];

        var trades = new List<RunTradeRecord>();
        // Per-asset pairing state: position leaves zero → entry, returns to zero → exit.
        var open = new Dictionary<string, OpenTrade>(StringComparer.Ordinal);
        // The registry emits slPlaced/tpPlaced BEFORE the engine flushes the entry ord.fill,
        // so protection seen with no open trade is held pending and consumed at trade open.
        var pending = new Dictionary<string, (long? Sl, long? Tp)>(StringComparer.Ordinal);

        await foreach (var line in storage.ReadLines(eventsKey, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.GetProperty("_t").GetString();
            if (type is not ("ord.fill" or "grp")) continue;

            var d = root.GetProperty("d");
            var asset = d.GetProperty("assetName").GetString()!;

            if (type == "grp")
            {
                var transition = d.GetProperty("transition").GetString();
                switch (transition)
                {
                    case "slPlaced" when open.TryGetValue(asset, out var t):
                        t.StopLoss = d.GetProperty("price").GetInt64();
                        break;
                    case "tpPlaced" when open.TryGetValue(asset, out var t):
                        t.TakeProfit = d.GetProperty("price").GetInt64();
                        break;
                    case "slPlaced":
                        pending[asset] = (d.GetProperty("price").GetInt64(), pending.GetValueOrDefault(asset).Tp);
                        break;
                    case "tpPlaced":
                        pending[asset] = (pending.GetValueOrDefault(asset).Sl, d.GetProperty("price").GetInt64());
                        break;
                    case "entryCancelled":
                        pending.Remove(asset); // entry never fills → orphaned protection must not leak
                        break;
                }
                continue;
            }

            var ts = DateTimeOffset.Parse(root.GetProperty("ts").GetString()!, CultureInfo.InvariantCulture);
            var side = d.GetProperty("side").GetString()!;
            var price = d.GetProperty("price").GetInt64();
            var quantity = ParseDecimal(d.GetProperty("quantity"));
            var commission = d.TryGetProperty("commission", out var c) ? c.GetInt64() : 0L;
            var signed = string.Equals(side, "buy", StringComparison.OrdinalIgnoreCase) ? quantity : -quantity;

            if (!open.TryGetValue(asset, out var trade))
            {
                var (pendingSl, pendingTp) = pending.GetValueOrDefault(asset);
                pending.Remove(asset);
                open[asset] = new OpenTrade
                {
                    EntryTime = ts,
                    EntryPrice = price,
                    Side = side,
                    Quantity = quantity,
                    Position = signed,
                    Commission = commission,
                    StopLoss = pendingSl,
                    TakeProfit = pendingTp,
                };
                continue;
            }

            trade.Position += signed;
            trade.Commission += commission;
            if (trade.Position != 0m)
                continue;

            trades.Add(trade.Close(ts, price));
            open.Remove(asset);
        }

        // Position still open at run end — report entry-only (chart draws the entry marker).
        foreach (var trade in open.Values)
            trades.Add(trade.ToOpenRecord());

        if (open.Count > 0)
            logger.LogDebug("{Count} trade(s) still open at end of {Path}", open.Count, runFolderPath);

        return trades;
    }

    // Quantities are serialized at full engine precision (can exceed 28 significant digits);
    // decimal.Parse rounds consistently, so equal-quantity entry/exit fills still cancel to zero.
    private static decimal ParseDecimal(JsonElement el) =>
        decimal.Parse(el.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private sealed class OpenTrade
    {
        public required DateTimeOffset EntryTime { get; init; }
        public required long EntryPrice { get; init; }
        public required string Side { get; init; }
        public required decimal Quantity { get; init; }
        public required decimal Position { get; set; }
        public required long Commission { get; set; }
        public long? StopLoss { get; set; }
        public long? TakeProfit { get; set; }

        public RunTradeRecord Close(DateTimeOffset exitTime, long exitPrice)
        {
            var direction = string.Equals(Side, "buy", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
            var gross = (exitPrice - EntryPrice) * direction * Quantity;
            return new RunTradeRecord
            {
                EntryTime = EntryTime,
                EntryPrice = EntryPrice,
                ExitTime = exitTime,
                ExitPrice = exitPrice,
                Side = Side,
                Quantity = Quantity,
                Pnl = MoneyConvert.ToLong(gross) - Commission,
                Commission = Commission,
                StopLossPrice = StopLoss,
                TakeProfitPrice = TakeProfit,
            };
        }

        public RunTradeRecord ToOpenRecord() => new()
        {
            EntryTime = EntryTime,
            EntryPrice = EntryPrice,
            Side = Side,
            Quantity = Quantity,
            Commission = Commission,
            StopLossPrice = StopLoss,
            TakeProfitPrice = TakeProfit,
        };
    }
}
