using System.Text.Json;
using AlgoTradeForge.Application.Events;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.Events;

public sealed class SqliteTradeDbWriter(
    IOptions<EventLogStorageOptions> storageOptions,
    IOptions<PostRunPipelineOptions> pipelineOptions) : ITradeDbWriter
{
    private sealed record OrderRow
    {
        public long OrderId { get; init; }
        public string AssetName { get; init; } = "";
        public string Side { get; init; } = "";
        public string Type { get; init; } = "";
        public string Quantity { get; init; } = "0";
        public long? LimitPrice { get; init; }
        public long? StopPrice { get; init; }
        public string Status { get; set; } = "placed";
        public string SubmittedAt { get; init; } = "";
    }

    private sealed record TradeRow
    {
        public long OrderId { get; init; }
        public string AssetName { get; init; } = "";
        public string Side { get; init; } = "";
        public long Price { get; init; }
        public string Quantity { get; init; } = "0";
        public long Commission { get; init; }
        public string Timestamp { get; init; } = "";
    }

    public void WriteFromJsonl(string runFolderPath, RunIdentity identity, RunSummary summary)
    {
        var dbPath = ResolveDbPath();
        EnsureSchema(dbPath);
        InsertRun(dbPath, runFolderPath, identity, summary);
    }

    public void RebuildFromJsonl(string runFolderPath, RunIdentity identity, RunSummary summary)
    {
        var dbPath = ResolveDbPath();
        EnsureSchema(dbPath);
        DeleteRun(dbPath, runFolderPath);
        InsertRun(dbPath, runFolderPath, identity, summary);
    }

    internal string ResolveDbPath()
    {
        if (pipelineOptions.Value.TradeDbPath is { } custom)
            return custom;

        // Default: sibling of EventLogs root → Data/trades.sqlite
        var root = storageOptions.Value.Root;
        var parent = Path.GetDirectoryName(root) ?? root;
        return Path.Combine(parent, "trades.sqlite");
    }

    private static void EnsureSchema(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        using var ddl = conn.CreateCommand();
        ddl.CommandText = """
            CREATE TABLE IF NOT EXISTS runs (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                run_folder    TEXT    NOT NULL UNIQUE,
                strategy      TEXT    NOT NULL,
                version       TEXT    NOT NULL,
                asset         TEXT    NOT NULL,
                start_time    TEXT    NOT NULL,
                end_time      TEXT    NOT NULL,
                initial_cash  INTEGER NOT NULL,
                mode          TEXT    NOT NULL,
                params_json   TEXT,
                total_bars    INTEGER NOT NULL,
                final_equity  INTEGER NOT NULL,
                total_fills   INTEGER NOT NULL,
                duration_ms   INTEGER NOT NULL,
                run_timestamp TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS orders (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id      INTEGER NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                order_id    INTEGER NOT NULL,
                asset       TEXT    NOT NULL,
                side        TEXT    NOT NULL,
                type        TEXT    NOT NULL,
                quantity    TEXT    NOT NULL,
                limit_price INTEGER,
                stop_price  INTEGER,
                status      TEXT    NOT NULL,
                submitted_at TEXT   NOT NULL
            );

            CREATE TABLE IF NOT EXISTS trades (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id      INTEGER NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                order_id    INTEGER NOT NULL,
                asset       TEXT    NOT NULL,
                side        TEXT    NOT NULL,
                price       INTEGER NOT NULL,
                quantity    TEXT    NOT NULL,
                commission  INTEGER NOT NULL,
                timestamp   TEXT    NOT NULL
            );
            """;
        ddl.ExecuteNonQuery();
    }

    private static void DeleteRun(string dbPath, string runFolderPath)
    {
        var runFolder = Path.GetFileName(runFolderPath);
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        using var tx = conn.BeginTransaction();
        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM runs WHERE run_folder = $folder";
        del.Parameters.AddWithValue("$folder", runFolder);
        del.ExecuteNonQuery();
        tx.Commit();
    }

    private void InsertRun(string dbPath, string runFolderPath, RunIdentity identity, RunSummary summary)
    {
        var eventsPath = Path.Combine(runFolderPath, "events.jsonl");
        var runFolder = Path.GetFileName(runFolderPath);

        // Scan JSONL for orders and trades
        var orders = new Dictionary<long, OrderRow>();
        var trades = new List<TradeRow>();

        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.GetProperty("_t").GetString()!;
            var ts = root.GetProperty("ts").GetString()!;

            switch (type)
            {
                case "ord.place":
                {
                    var d = root.GetProperty("d");
                    var orderId = d.GetProperty("orderId").GetInt64();
                    orders[orderId] = new OrderRow
                    {
                        OrderId = orderId,
                        AssetName = d.GetProperty("assetName").GetString()!,
                        Side = d.GetProperty("side").GetString()!,
                        Type = d.GetProperty("type").GetString()!,
                        Quantity = d.GetProperty("quantity").GetRawText(),
                        LimitPrice = d.TryGetProperty("limitPrice", out var lp) && lp.ValueKind != JsonValueKind.Null ? lp.GetInt64() : null,
                        StopPrice = d.TryGetProperty("stopPrice", out var sp) && sp.ValueKind != JsonValueKind.Null ? sp.GetInt64() : null,
                        Status = "placed",
                        SubmittedAt = ts,
                    };
                    break;
                }
                case "ord.fill":
                {
                    var d = root.GetProperty("d");
                    var orderId = d.GetProperty("orderId").GetInt64();
                    if (orders.TryGetValue(orderId, out var o))
                        o.Status = "filled";

                    trades.Add(new TradeRow
                    {
                        OrderId = orderId,
                        AssetName = d.GetProperty("assetName").GetString()!,
                        Side = d.GetProperty("side").GetString()!,
                        Price = d.GetProperty("price").GetInt64(),
                        Quantity = d.GetProperty("quantity").GetRawText(),
                        Commission = d.GetProperty("commission").GetInt64(),
                        Timestamp = ts,
                    });
                    break;
                }
                case "ord.cancel":
                {
                    var d = root.GetProperty("d");
                    var orderId = d.GetProperty("orderId").GetInt64();
                    if (orders.TryGetValue(orderId, out var o))
                        o.Status = "cancelled";
                    break;
                }
                case "ord.reject":
                {
                    var d = root.GetProperty("d");
                    var orderId = d.GetProperty("orderId").GetInt64();
                    if (orders.TryGetValue(orderId, out var o))
                        o.Status = "rejected";
                    break;
                }
            }
        }

        // Insert into DB
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        using var tx = conn.BeginTransaction();

        // Insert run
        using var insertRun = conn.CreateCommand();
        insertRun.CommandText = """
            INSERT INTO runs (run_folder, strategy, version, asset, start_time, end_time,
                              initial_cash, mode, params_json, total_bars, final_equity,
                              total_fills, duration_ms, run_timestamp)
            VALUES ($folder, $strategy, $version, $asset, $start, $end,
                    $cash, $mode, $params, $bars, $equity,
                    $fills, $duration, $runTs)
            RETURNING id
            """;
        insertRun.Parameters.AddWithValue("$folder", runFolder);
        insertRun.Parameters.AddWithValue("$strategy", identity.StrategyName);
        insertRun.Parameters.AddWithValue("$version", identity.StrategyVersion);
        insertRun.Parameters.AddWithValue("$asset", identity.AssetName);
        insertRun.Parameters.AddWithValue("$start", identity.StartTime.ToString("o"));
        insertRun.Parameters.AddWithValue("$end", identity.EndTime.ToString("o"));
        insertRun.Parameters.AddWithValue("$cash", identity.InitialCash);
        insertRun.Parameters.AddWithValue("$mode", identity.RunMode.ToString());
        insertRun.Parameters.AddWithValue("$params",
            identity.StrategyParameters is { Count: > 0 }
                ? JsonSerializer.Serialize(identity.StrategyParameters)
                : (object)DBNull.Value);
        insertRun.Parameters.AddWithValue("$bars", summary.TotalBarsProcessed);
        insertRun.Parameters.AddWithValue("$equity", summary.FinalEquity);
        insertRun.Parameters.AddWithValue("$fills", summary.TotalFills);
        insertRun.Parameters.AddWithValue("$duration", (long)summary.Duration.TotalMilliseconds);
        insertRun.Parameters.AddWithValue("$runTs", identity.RunTimestamp.ToString("o"));

        var runId = (long)insertRun.ExecuteScalar()!;

        // Insert orders
        if (orders.Count > 0)
        {
            using var insertOrder = conn.CreateCommand();
            insertOrder.CommandText = """
                INSERT INTO orders (run_id, order_id, asset, side, type, quantity, limit_price, stop_price, status, submitted_at)
                VALUES ($runId, $orderId, $asset, $side, $type, $qty, $limit, $stop, $status, $submitted)
                """;

            var pRunId = insertOrder.CreateParameter(); pRunId.ParameterName = "$runId"; insertOrder.Parameters.Add(pRunId);
            var pOrderId = insertOrder.CreateParameter(); pOrderId.ParameterName = "$orderId"; insertOrder.Parameters.Add(pOrderId);
            var pAsset = insertOrder.CreateParameter(); pAsset.ParameterName = "$asset"; insertOrder.Parameters.Add(pAsset);
            var pSide = insertOrder.CreateParameter(); pSide.ParameterName = "$side"; insertOrder.Parameters.Add(pSide);
            var pType = insertOrder.CreateParameter(); pType.ParameterName = "$type"; insertOrder.Parameters.Add(pType);
            var pQty = insertOrder.CreateParameter(); pQty.ParameterName = "$qty"; insertOrder.Parameters.Add(pQty);
            var pLimit = insertOrder.CreateParameter(); pLimit.ParameterName = "$limit"; insertOrder.Parameters.Add(pLimit);
            var pStop = insertOrder.CreateParameter(); pStop.ParameterName = "$stop"; insertOrder.Parameters.Add(pStop);
            var pStatus = insertOrder.CreateParameter(); pStatus.ParameterName = "$status"; insertOrder.Parameters.Add(pStatus);
            var pSubmitted = insertOrder.CreateParameter(); pSubmitted.ParameterName = "$submitted"; insertOrder.Parameters.Add(pSubmitted);
            insertOrder.Prepare();

            foreach (var order in orders.Values)
            {
                pRunId.Value = runId;
                pOrderId.Value = order.OrderId;
                pAsset.Value = order.AssetName;
                pSide.Value = order.Side;
                pType.Value = order.Type;
                pQty.Value = order.Quantity;
                pLimit.Value = order.LimitPrice.HasValue ? order.LimitPrice.Value : DBNull.Value;
                pStop.Value = order.StopPrice.HasValue ? order.StopPrice.Value : DBNull.Value;
                pStatus.Value = order.Status;
                pSubmitted.Value = order.SubmittedAt;
                insertOrder.ExecuteNonQuery();
            }
        }

        // Insert trades
        if (trades.Count > 0)
        {
            using var insertTrade = conn.CreateCommand();
            insertTrade.CommandText = """
                INSERT INTO trades (run_id, order_id, asset, side, price, quantity, commission, timestamp)
                VALUES ($runId, $orderId, $asset, $side, $price, $qty, $commission, $ts)
                """;

            var tRunId = insertTrade.CreateParameter(); tRunId.ParameterName = "$runId"; insertTrade.Parameters.Add(tRunId);
            var tOrderId = insertTrade.CreateParameter(); tOrderId.ParameterName = "$orderId"; insertTrade.Parameters.Add(tOrderId);
            var tAsset = insertTrade.CreateParameter(); tAsset.ParameterName = "$asset"; insertTrade.Parameters.Add(tAsset);
            var tSide = insertTrade.CreateParameter(); tSide.ParameterName = "$side"; insertTrade.Parameters.Add(tSide);
            var tPrice = insertTrade.CreateParameter(); tPrice.ParameterName = "$price"; insertTrade.Parameters.Add(tPrice);
            var tQty = insertTrade.CreateParameter(); tQty.ParameterName = "$qty"; insertTrade.Parameters.Add(tQty);
            var tCommission = insertTrade.CreateParameter(); tCommission.ParameterName = "$commission"; insertTrade.Parameters.Add(tCommission);
            var tTs = insertTrade.CreateParameter(); tTs.ParameterName = "$ts"; insertTrade.Parameters.Add(tTs);
            insertTrade.Prepare();

            foreach (var trade in trades)
            {
                tRunId.Value = runId;
                tOrderId.Value = trade.OrderId;
                tAsset.Value = trade.AssetName;
                tSide.Value = trade.Side;
                tPrice.Value = trade.Price;
                tQty.Value = trade.Quantity;
                tCommission.Value = trade.Commission;
                tTs.Value = trade.Timestamp;
                insertTrade.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }
}
