using System.Net;

namespace AlgoTradeForge.HistoryLoader.Application;

/// <summary>
/// Represents an API error from a data source (e.g. Binance) where the response body
/// contains a structured error code and message.
/// </summary>
public sealed class DataSourceApiException : HttpRequestException
{
    public int ApiErrorCode { get; }
    public string ApiErrorMessage { get; }

    /// <summary>
    /// Binance error codes -1100 through -1130 indicate parameter validation failures
    /// (ILLEGAL_CHARS, BAD_SYMBOL, BAD_INTERVAL, etc.) that should not be retried.
    /// </summary>
    public bool IsParameterValidationError => ApiErrorCode is >= -1130 and <= -1100;

    public DataSourceApiException(int apiErrorCode, string apiErrorMessage, HttpStatusCode statusCode)
        : base($"Data source error {apiErrorCode}: {apiErrorMessage}", inner: null, statusCode)
    {
        ApiErrorCode = apiErrorCode;
        ApiErrorMessage = apiErrorMessage;
    }
}
