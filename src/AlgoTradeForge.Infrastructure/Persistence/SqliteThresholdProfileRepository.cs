using AlgoTradeForge.Application.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.Persistence;

public sealed class SqliteThresholdProfileRepository(
    IOptions<RunStorageOptions> options) : IThresholdProfileRepository
{
    private string ConnectionString => $"Data Source={options.Value.DatabasePath}";
    private bool _initialized;

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await SqliteDbInitializer.EnsureCreatedAsync(ConnectionString);
        _initialized = true;
    }

    public async Task<IReadOnlyList<ThresholdProfileRecord>> ListAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, profile_json, is_builtin, created_at, updated_at FROM threshold_profiles ORDER BY name";

        var results = new List<ThresholdProfileRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRecord(reader));

        return results;
    }

    public async Task<ThresholdProfileRecord?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, profile_json, is_builtin, created_at, updated_at FROM threshold_profiles WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task SaveAsync(ThresholdProfileRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO threshold_profiles (name, profile_json, is_builtin, created_at, updated_at)
            VALUES ($name, $profileJson, $isBuiltIn, $createdAt, $updatedAt)
            ON CONFLICT(name) DO UPDATE SET
                profile_json = $profileJson,
                updated_at = $updatedAt
            """;
        cmd.Parameters.AddWithValue("$name", record.Name);
        cmd.Parameters.AddWithValue("$profileJson", record.ProfileJson);
        cmd.Parameters.AddWithValue("$isBuiltIn", record.IsBuiltIn ? 1 : 0);
        cmd.Parameters.AddWithValue("$createdAt", record.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updatedAt", record.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM threshold_profiles WHERE name = $name AND is_builtin = 0";
        cmd.Parameters.AddWithValue("$name", name);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static ThresholdProfileRecord ReadRecord(SqliteDataReader reader) => new()
    {
        Name = reader.GetString(0),
        ProfileJson = reader.GetString(1),
        IsBuiltIn = reader.GetInt32(2) != 0,
        CreatedAt = DateTimeOffset.Parse(reader.GetString(3)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(4)),
    };
}
