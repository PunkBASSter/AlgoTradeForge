using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Backtests;

public sealed record BacktestSetup(
    Asset Asset,
    decimal ScaleFactor,
    BacktestOptions Options,
    IInt64BarStrategy Strategy,
    TimeSeries<Int64Bar>[] Series);
