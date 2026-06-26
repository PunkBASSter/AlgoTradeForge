using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

// Gates the live paper integration tests. Configure via env vars to run locally against the gnzsnz
// ib-gateway compose stack; absent => the tests are skipped (CI has no gateway).
internal static class IbPaperGatewayConfig
{
    public static string? Host => Environment.GetEnvironmentVariable("IB_PAPER_HOST");
    public static int Port => int.TryParse(Environment.GetEnvironmentVariable("IB_PAPER_PORT"), out var p) ? p : 4004;
    public static int ClientId =>
        int.TryParse(Environment.GetEnvironmentVariable("IB_PAPER_CLIENT_ID"), out var c) ? c : 11;

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public static IbConnectionOptions Options => new(Host!, Port, ClientId);

    public const string SkipReason =
        "IB paper gateway not configured. Start the gnzsnz ib-gateway stack and set IB_PAPER_HOST " +
        "(and optionally IB_PAPER_PORT=4004, IB_PAPER_CLIENT_ID) to run these integration tests.";
}
