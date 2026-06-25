using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Locates the source-record stream to replay. <see cref="Asset"/> resolves the on-disk asset dir
/// via the shared Infrastructure naming (AssetDirectoryName); <see cref="FromTs"/> is the resume
/// boundary (last completed bar's open ts).
/// </summary>
public readonly record struct ReplayRequest(Asset Asset, string Venue, string SourceFeedId, long FromTs);
