using System.Net;

namespace AlgoTradeForge.HistoryLoader.Application;

/// <summary>
/// Represents a structured API error from a data source where the response body
/// contains an error code and message. Exchange-agnostic — classification logic
/// (e.g. whether this is a date-range error) belongs in the exchange-specific adapter.
/// </summary>
public sealed class DataSourceApiException : HttpRequestException
{
    public int ApiErrorCode { get; }
    public string ApiErrorMessage { get; }

    /// <summary>
    /// Whether this error indicates the requested time range is invalid and advancing
    /// the start date might resolve it. Set by the exchange-specific adapter that
    /// understands the error format.
    /// </summary>
    public bool IsDateRangeError { get; }

    public DataSourceApiException(
        int apiErrorCode, string apiErrorMessage, HttpStatusCode statusCode,
        bool isDateRangeError = false)
        : base($"Data source error {apiErrorCode}: {apiErrorMessage}", inner: null, statusCode)
    {
        ApiErrorCode = apiErrorCode;
        ApiErrorMessage = apiErrorMessage;
        IsDateRangeError = isDateRangeError;
    }
}
