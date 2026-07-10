namespace AlgoTradeForge.HistoryLoader.Domain.Symbology;

public static class CanonicalSymbolParser
{
    /// <summary>Grammar: BASE/QUOTE | BASE/QUOTE-PERP | BASE/QUOTE-FUT-YYYY-MM. "-OPT-" is a
    /// reserved suffix → explicit error. Tokens: [A-Z0-9]{1,20}. Round-trip: Format(Parse(s)) == s.</summary>
    public static bool TryParse(string input, out CanonicalSymbol? symbol, out string? error)
    {
        symbol = null;
        error  = null;

        if (string.IsNullOrEmpty(input))
        {
            error = "input is empty";
            return false;
        }

        int slashIdx = input.IndexOf('/');
        if (slashIdx < 0)
        {
            error = $"missing '/' separator in '{input}'";
            return false;
        }

        var baseToken = input[..slashIdx];
        var rest      = input[(slashIdx + 1)..];

        if (!IsValidToken(baseToken, out var baseErr))
        {
            error = $"invalid base '{baseToken}': {baseErr}";
            return false;
        }

        int dashIdx = rest.IndexOf('-');
        string quoteToken;
        string? suffix;

        if (dashIdx < 0)
        {
            quoteToken = rest;
            suffix     = null;
        }
        else
        {
            quoteToken = rest[..dashIdx];
            suffix     = rest[(dashIdx + 1)..];
        }

        if (!IsValidToken(quoteToken, out var quoteErr))
        {
            error = $"invalid quote '{quoteToken}': {quoteErr}";
            return false;
        }

        if (suffix is null)
        {
            symbol = new CanonicalSymbol(baseToken, quoteToken, InstrumentKind.Spot, null);
            return true;
        }

        if (suffix.StartsWith("OPT", StringComparison.Ordinal))
        {
            error = "options instruments are reserved, not yet supported";
            return false;
        }

        if (suffix == "PERP")
        {
            symbol = new CanonicalSymbol(baseToken, quoteToken, InstrumentKind.Perpetual, null);
            return true;
        }

        if (suffix.StartsWith("FUT-", StringComparison.Ordinal))
        {
            var expiry = suffix[4..];
            if (!IsValidExpiry(expiry, out var expiryErr))
            {
                error = $"invalid expiry '{expiry}': {expiryErr}";
                return false;
            }
            symbol = new CanonicalSymbol(baseToken, quoteToken, InstrumentKind.DatedFuture, expiry);
            return true;
        }

        error = $"unknown suffix '-{suffix}'";
        return false;
    }

    private static bool IsValidToken(string token, out string? error)
    {
        error = null;
        if (token.Length == 0)
        {
            error = "empty token";
            return false;
        }
        if (token.Length > 20)
        {
            error = "exceeds 20 characters";
            return false;
        }
        foreach (char c in token)
        {
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            {
                error = $"invalid character '{c}' (only [A-Z0-9] allowed)";
                return false;
            }
        }
        return true;
    }

    // Validates YYYY-MM in range 2000-01..2099-12.
    private static bool IsValidExpiry(string expiry, out string? error)
    {
        error = null;
        if (expiry.Length != 7 || expiry[4] != '-')
        {
            error = "expected YYYY-MM format";
            return false;
        }
        if (!int.TryParse(expiry[..4], out int year) || !int.TryParse(expiry[5..], out int month))
        {
            error = "non-numeric year or month";
            return false;
        }
        if (year < 2000 || year > 2099)
        {
            error = "year must be 2000..2099";
            return false;
        }
        if (month < 1 || month > 12)
        {
            error = "month must be 01..12";
            return false;
        }
        return true;
    }
}
