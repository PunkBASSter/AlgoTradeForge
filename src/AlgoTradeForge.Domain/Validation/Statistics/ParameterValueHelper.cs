namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Shared utilities for extracting numeric values from parameter dictionaries.
/// Used by <see cref="ClusterAnalyzer"/> and <see cref="ParameterSensitivityAnalyzer"/>.
/// </summary>
internal static class ParameterValueHelper
{
    public static bool IsNumeric(object value) => value is
        int or long or float or double or decimal or short or byte or sbyte or uint or ulong or ushort;

    public static double ToDouble(object value) => value switch
    {
        int v => v,
        long v => v,
        float v => v,
        double v => v,
        decimal v => (double)v,
        short v => v,
        byte v => v,
        sbyte v => v,
        uint v => v,
        ulong v => v,
        ushort v => v,
        _ => 0,
    };
}
