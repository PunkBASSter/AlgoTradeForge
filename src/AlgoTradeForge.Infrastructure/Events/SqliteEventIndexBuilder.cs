using System.Text.Json;
using AlgoTradeForge.Application.Events;
using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.Infrastructure.Events;

public sealed class SqliteEventIndexBuilder : IEventIndexBuilder
{
    private const string IndexFileName = "index.sqlite";
    private const string TmpSuffix = ".tmp";
    private const string EventsFileName = "events.jsonl";

    public void Build(string runFolderPath)
    {
        var indexPath = Path.Combine(runFolderPath, IndexFileName);
        if (File.Exists(indexPath))
            return;

        BuildInternal(runFolderPath, indexPath);
    }

    public void Rebuild(string runFolderPath)
    {
        var indexPath = Path.Combine(runFolderPath, IndexFileName);
        if (File.Exists(indexPath))
        {
            // Clear pooled connections that may hold a handle to the file
            SqliteConnection.ClearAllPools();
            File.Delete(indexPath);
        }

        BuildInternal(runFolderPath, indexPath);
    }

    private static void BuildInternal(string runFolderPath, string indexPath)
    {
        var eventsPath = Path.Combine(runFolderPath, EventsFileName);
        var tmpPath = indexPath + TmpSuffix;

        try
        {
            using var connection = new SqliteConnection($"Data Source={tmpPath};Pooling=False");
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();
            }

            using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText = """
                    CREATE TABLE events (
                        sq  INTEGER NOT NULL,
                        ts  TEXT    NOT NULL,
                        _t  TEXT    NOT NULL,
                        src TEXT    NOT NULL,
                        raw TEXT    NOT NULL
                    );
                    CREATE INDEX ix_events_sq  ON events (sq);
                    CREATE INDEX ix_events_ts  ON events (ts);
                    CREATE INDEX ix_events_t   ON events (_t);
                    CREATE INDEX ix_events_src ON events (src);
                    """;
                ddl.ExecuteNonQuery();
            }

            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO events (sq, ts, _t, src, raw) VALUES ($sq, $ts, $t, $src, $raw)";

            var pSq = insert.CreateParameter(); pSq.ParameterName = "$sq"; insert.Parameters.Add(pSq);
            var pTs = insert.CreateParameter(); pTs.ParameterName = "$ts"; insert.Parameters.Add(pTs);
            var pT = insert.CreateParameter(); pT.ParameterName = "$t"; insert.Parameters.Add(pT);
            var pSrc = insert.CreateParameter(); pSrc.ParameterName = "$src"; insert.Parameters.Add(pSrc);
            var pRaw = insert.CreateParameter(); pRaw.ParameterName = "$raw"; insert.Parameters.Add(pRaw);
            insert.Prepare();

            foreach (var line in File.ReadLines(eventsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                pSq.Value = root.GetProperty("sq").GetInt64();
                pTs.Value = root.GetProperty("ts").GetString()!;
                pT.Value = root.GetProperty("_t").GetString()!;
                pSrc.Value = root.GetProperty("src").GetString()!;
                pRaw.Value = line;

                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            connection.Close();

            File.Move(tmpPath, indexPath);
        }
        catch
        {
            // Transactional safety: no partial index on disk
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
            throw;
        }
    }
}
