namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

public enum DiscontinuityReason
{
    /// <summary>Source records were lost (connection dropped) and could not be recovered.</summary>
    Disconnect,
    /// <summary>The archive had no records to bridge a detected aggId gap within budget.</summary>
    MissingArchive,
}
