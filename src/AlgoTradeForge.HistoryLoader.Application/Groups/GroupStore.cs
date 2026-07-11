using System.Text.Json;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

public sealed class GroupStore : IGroupStore
{
    private readonly IFileStorage _fs;
    private readonly HistoryLoaderOptions _options;
    private readonly ILogger<GroupStore> _logger;

    public event Action? GroupsChanged;

    public GroupStore(IFileStorage fs, IOptions<HistoryLoaderOptions> options, ILogger<GroupStore> logger)
    {
        _fs      = fs;
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<IReadOnlyList<GroupDocument>> List(CancellationToken ct = default)
    {
        var prefix = GroupsDir();
        var result = new List<GroupDocument>();

        await foreach (var key in _fs.ListKeys(prefix, suffix: ".json", recursive: false, ct: ct))
        {
            var stored = await _fs.ReadWithEtag(key, ct);
            if (stored is null) continue;

            try
            {
                var group = JsonSerializer.Deserialize<CollectionGroup>(stored.Content, GroupJson.Options);
                if (group is null)
                {
                    _logger.LogWarning("Group file '{Key}' deserialized to null; skipping.", key);
                    continue;
                }
                result.Add(new GroupDocument(group, stored.ETag));
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Group file '{Key}' is corrupt; skipping.", key);
            }
        }

        return result;
    }

    public async Task<GroupDocument?> Get(string name, CancellationToken ct = default)
    {
        ValidateName(name);
        var stored = await _fs.ReadWithEtag(GroupKey(name), ct);
        if (stored is null) return null;

        try
        {
            var group = JsonSerializer.Deserialize<CollectionGroup>(stored.Content, GroupJson.Options);
            return group is null ? null : new GroupDocument(group, stored.ETag);
        }
        catch (JsonException ex)
        {
            throw new GroupValidationException([$"group file '{name}.json' is not valid JSON: {ex.Message}"]);
        }
    }

    public async Task<string> Put(string name, CollectionGroup group, string? expectedETag, CancellationToken ct = default)
    {
        ValidateName(name);

        var errors = new List<string>();
        if (group.Name != name)
            errors.Add($"group.Name '{group.Name}' must equal file name '{name}'");
        errors.AddRange(GroupValidator.Validate(group));

        if (errors.Count > 0) throw new GroupValidationException(errors);

        var content = JsonSerializer.Serialize(group, GroupJson.Options);
        var newEtag = await _fs.WriteIfMatch(GroupKey(name), content, expectedETag, ct);
        GroupsChanged?.Invoke();
        return newEtag;
    }

    public async Task<bool> Delete(string name, CancellationToken ct = default)
    {
        ValidateName(name);
        var key = GroupKey(name);
        if (!await _fs.Exists(key, ct)) return false;
        await _fs.Delete(key, ct);
        GroupsChanged?.Invoke();
        return true;
    }

    // -------------------------------------------------------------------------

    private string ConfigRoot() =>
        _options.ConfigRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlgoTradeForge", "HistoryConfig");

    private string GroupsDir() => Path.Combine(ConfigRoot(), "groups");

    private string GroupKey(string name) => Path.Combine(GroupsDir(), $"{name}.json");

    // Path-traversal guard: name regex allows only [a-z0-9_-], ruling out '.', '/', '\', etc.
    private static void ValidateName(string name)
    {
        if (!GroupName.IsValid(name))
            throw new ArgumentException(
                $"name '{name}' does not match ^[a-z0-9][a-z0-9_-]{{0,63}}$", nameof(name));
    }
}
