using System.Text.Json;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Validation;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class ThresholdProfileEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void MapThresholdProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/threshold-profiles")
            .WithTags("ThresholdProfiles");

        group.MapGet("/", ListProfiles)
            .WithName("ListThresholdProfiles")
            .WithSummary("List all threshold profiles (built-in + custom)")
            .WithOpenApi();

        group.MapGet("/{name}", GetProfile)
            .WithName("GetThresholdProfile")
            .WithSummary("Get a single threshold profile by name")
            .WithOpenApi();

        group.MapPost("/", CreateProfile)
            .WithName("CreateThresholdProfile")
            .WithSummary("Create a custom threshold profile")
            .WithOpenApi();

        group.MapPut("/{name}", UpdateProfile)
            .WithName("UpdateThresholdProfile")
            .WithSummary("Update a custom threshold profile")
            .WithOpenApi();

        group.MapDelete("/{name}", DeleteProfile)
            .WithName("DeleteThresholdProfile")
            .WithSummary("Delete a custom threshold profile")
            .WithOpenApi();
    }

    private static async Task<IResult> ListProfiles(IThresholdProfileRepository repository, CancellationToken ct)
    {
        var custom = await repository.ListAsync(ct);

        // Merge built-in profiles with custom ones
        var builtIn = ValidationThresholdProfile.BuiltInNames
            .Where(name => custom.All(c => c.Name != name))
            .Select(name =>
            {
                var profile = ValidationThresholdProfile.GetByName(name);
                return new ThresholdProfileResponse
                {
                    Name = name,
                    IsBuiltIn = true,
                    ProfileJson = JsonSerializer.Serialize(profile, JsonOptions),
                };
            });

        var customResponses = custom.Select(c => new ThresholdProfileResponse
        {
            Name = c.Name,
            IsBuiltIn = c.IsBuiltIn,
            ProfileJson = c.ProfileJson,
        });

        return Results.Ok(builtIn.Concat(customResponses).OrderBy(p => p.Name));
    }

    private static async Task<IResult> GetProfile(
        string name, IThresholdProfileRepository repository, CancellationToken ct)
    {
        // Try built-in first
        try
        {
            var builtIn = ValidationThresholdProfile.GetByName(name);
            return Results.Ok(new ThresholdProfileResponse
            {
                Name = name,
                IsBuiltIn = true,
                ProfileJson = JsonSerializer.Serialize(builtIn, JsonOptions),
            });
        }
        catch (ArgumentException) { /* not a built-in, try custom */ }

        var custom = await repository.GetByNameAsync(name, ct);
        if (custom is null) return Results.NotFound();

        return Results.Ok(new ThresholdProfileResponse
        {
            Name = custom.Name,
            IsBuiltIn = custom.IsBuiltIn,
            ProfileJson = custom.ProfileJson,
        });
    }

    private static async Task<IResult> CreateProfile(
        CreateThresholdProfileRequest request,
        IThresholdProfileRepository repository,
        CancellationToken ct)
    {
        // Validate name doesn't conflict with built-in
        if (ValidationThresholdProfile.BuiltInNames.Contains(request.Name))
            return Results.BadRequest("Cannot create a profile with a built-in name.");

        // Deserialize and validate against safety floors
        ValidationThresholdProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<ValidationThresholdProfile>(request.ProfileJson, JsonOptions)
                ?? throw new JsonException("Deserialization returned null.");
        }
        catch (JsonException ex)
        {
            return Results.BadRequest($"Invalid profile JSON: {ex.Message}");
        }

        var violations = ThresholdProfileValidator.Validate(profile);
        if (violations.Count > 0)
            return Results.BadRequest(new { Violations = violations });

        var now = DateTimeOffset.UtcNow;
        await repository.SaveAsync(new ThresholdProfileRecord
        {
            Name = request.Name,
            ProfileJson = request.ProfileJson,
            IsBuiltIn = false,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);

        return Results.Created($"/api/threshold-profiles/{request.Name}", new ThresholdProfileResponse
        {
            Name = request.Name,
            IsBuiltIn = false,
            ProfileJson = request.ProfileJson,
        });
    }

    private static async Task<IResult> UpdateProfile(
        string name,
        UpdateThresholdProfileRequest request,
        IThresholdProfileRepository repository,
        CancellationToken ct)
    {
        if (ValidationThresholdProfile.BuiltInNames.Contains(name))
            return Results.BadRequest("Cannot modify a built-in profile.");

        var existing = await repository.GetByNameAsync(name, ct);
        if (existing is null) return Results.NotFound();

        ValidationThresholdProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<ValidationThresholdProfile>(request.ProfileJson, JsonOptions)
                ?? throw new JsonException("Deserialization returned null.");
        }
        catch (JsonException ex)
        {
            return Results.BadRequest($"Invalid profile JSON: {ex.Message}");
        }

        var violations = ThresholdProfileValidator.Validate(profile);
        if (violations.Count > 0)
            return Results.BadRequest(new { Violations = violations });

        await repository.SaveAsync(new ThresholdProfileRecord
        {
            Name = name,
            ProfileJson = request.ProfileJson,
            IsBuiltIn = false,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);

        return Results.Ok(new ThresholdProfileResponse
        {
            Name = name,
            IsBuiltIn = false,
            ProfileJson = request.ProfileJson,
        });
    }

    private static async Task<IResult> DeleteProfile(
        string name, IThresholdProfileRepository repository, CancellationToken ct)
    {
        if (ValidationThresholdProfile.BuiltInNames.Contains(name))
            return Results.BadRequest("Cannot delete a built-in profile.");

        var deleted = await repository.DeleteAsync(name, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}

public sealed record CreateThresholdProfileRequest
{
    public required string Name { get; init; }
    public required string ProfileJson { get; init; }
}

public sealed record UpdateThresholdProfileRequest
{
    public required string ProfileJson { get; init; }
}

public sealed record ThresholdProfileResponse
{
    public required string Name { get; init; }
    public required bool IsBuiltIn { get; init; }
    public required string ProfileJson { get; init; }
}
