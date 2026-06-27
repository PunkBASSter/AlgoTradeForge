namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Maps the host "Venue" config key to the active venue. One venue per LiveHost process
// (service-decomposition: N instances by venue class). Plan 5 maps ATF_PROFILE -> this key.
public static class VenueSelector
{
    public static VenueKind Parse(string? venue) => (venue ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "binance" => VenueKind.Binance,
        "ib" => VenueKind.Ib,
        var other => throw new ArgumentException($"Unknown Venue '{other}'. Expected 'binance' or 'ib'."),
    };
}
