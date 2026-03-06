namespace AlgoTradeForge.Application.Live;

public interface ILiveSessionDataProvider
{
    LiveSessionSnapshot? GetSnapshot(Guid sessionId);
}
