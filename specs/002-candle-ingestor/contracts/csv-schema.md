# CSV Partition File Schema

**Version**: 1.0

## File Path Convention

```
{DataRoot}/{Exchange}/{Symbol}/{Year}/{YYYY-MM}.csv
```

Example: `Data/Candles/Binance/BTCUSDT/2024/2024-01.csv`

## Header

```
Timestamp,Open,High,Low,Close,Volume
```

## Row Format

```
{ISO 8601 DateTimeOffset UTC},{long},{long},{long},{long},{long}
```

## Column Definitions

| Column    | Type             | Description                                    |
|-----------|------------------|------------------------------------------------|
| Timestamp | DateTimeOffset   | Candle open time, ISO 8601 UTC (+00:00 offset) |
| Open      | long             | Open price × 10^DecimalDigits                  |
| High      | long             | High price × 10^DecimalDigits                  |
| Low       | long             | Low price × 10^DecimalDigits                   |
| Close     | long             | Close price × 10^DecimalDigits                 |
| Volume    | long             | Volume × 10^DecimalDigits                      |

## Example

```csv
Timestamp,Open,High,Low,Close,Volume
2024-01-15T00:00:00+00:00,6743215,6745100,6741000,6744300,153240000
2024-01-15T00:01:00+00:00,6744300,6746500,6743800,6745900,98760000
```

## Constraints

- Rows MUST be sorted by Timestamp ascending
- No duplicate timestamps within a file
- DecimalDigits is NOT stored in the file — it is carried in asset configuration
- Files for completed months are immutable
- Only the current (latest) month file is appended to
