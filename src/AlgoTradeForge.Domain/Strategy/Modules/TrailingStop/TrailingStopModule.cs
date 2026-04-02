using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules.TrailingStop;

[ModuleKey("trailing-stop")]
public sealed class TrailingStopModule(TrailingStopParams parameters)
    : IStrategyModule<TrailingStopParams>
{
    private readonly Dictionary<long, TrailingStopState> _states = [];

    public void Activate(long groupId, long entryPrice, OrderSide direction, long initialStop)
    {
        _states[groupId] = new TrailingStopState
        {
            CurrentStop = initialStop,
            Direction = direction,
            ActivationPrice = entryPrice,
            HighWaterMark = entryPrice
        };
    }

    /// <summary>
    /// Updates the trailing stop for a position.
    /// </summary>
    /// <param name="groupId">The order group ID.</param>
    /// <param name="bar">Current price bar.</param>
    /// <param name="currentAtr">Current ATR value from StrategyContext. Used by Atr/Chandelier variants;
    /// falls back to 2% of watermark when zero (indicator not yet warmed up).</param>
    /// <returns>New stop price if changed, null otherwise.</returns>
    public long? Update(long groupId, Int64Bar bar, long currentAtr = 0)
    {
        if (!_states.TryGetValue(groupId, out var state))
            return null;

        var prevStop = state.CurrentStop;

        if (state.Direction == OrderSide.Buy)
        {
            // Long: track high water mark, stop ratchets up
            if (bar.High > state.HighWaterMark)
                state.HighWaterMark = bar.High;

            var newStop = ComputeStop(state.HighWaterMark, state.Direction, currentAtr);
            if (newStop > state.CurrentStop)
                state.CurrentStop = newStop;
        }
        else
        {
            // Short: track low water mark, stop ratchets down
            if (bar.Low < state.HighWaterMark || state.HighWaterMark == state.ActivationPrice)
            {
                if (state.HighWaterMark == state.ActivationPrice)
                    state.HighWaterMark = bar.Low;
                else if (bar.Low < state.HighWaterMark)
                    state.HighWaterMark = bar.Low;
            }

            var newStop = ComputeStop(state.HighWaterMark, state.Direction, currentAtr);
            if (newStop < state.CurrentStop)
                state.CurrentStop = newStop;
        }

        _states[groupId] = state;
        return state.CurrentStop != prevStop ? state.CurrentStop : null;
    }

    public long? GetCurrentStop(long groupId) =>
        _states.TryGetValue(groupId, out var state) ? state.CurrentStop : null;

    public void Remove(long groupId) => _states.Remove(groupId);

    private long ComputeStop(long waterMark, OrderSide direction, long currentAtr)
    {
        var atr = currentAtr > 0 ? currentAtr : waterMark / 50; // fallback: ~2% of price
        var distance = (long)(parameters.AtrMultiplier * atr);

        return direction == OrderSide.Buy
            ? waterMark - distance
            : waterMark + distance;
    }
}
