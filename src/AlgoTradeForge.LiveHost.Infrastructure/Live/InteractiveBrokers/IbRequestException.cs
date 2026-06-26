namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal sealed class IbRequestException(int errorCode, string errorMessage)
    : Exception($"IB request failed (code {errorCode}): {errorMessage}")
{
    public int ErrorCode { get; } = errorCode;
    public string ErrorMessage { get; } = errorMessage;
}
