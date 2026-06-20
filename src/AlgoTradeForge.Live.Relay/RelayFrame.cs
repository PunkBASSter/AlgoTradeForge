using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

// Canonical per-frame event time; equals TradeTick.TimestampMs for Tick frames.
public readonly record struct RelayFrame(FrameType Type, long TimestampMs, TradeTick Trade, byte ReasonCode);
