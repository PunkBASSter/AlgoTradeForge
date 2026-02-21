using System.Text.Json;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Tests.TestUtilities;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Events;

public class EventSerializationTests
{
    private static readonly DateTimeOffset Ts = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    #region TypeId and DefaultExportMode

    [Fact] public void BarEvent_TypeId() => Assert.Equal("bar", BarEvent.TypeId);
    [Fact] public void BarMutationEvent_TypeId() => Assert.Equal("bar.mut", BarMutationEvent.TypeId);
    [Fact] public void TickEvent_TypeId() => Assert.Equal("tick", TickEvent.TypeId);
    [Fact] public void IndicatorEvent_TypeId() => Assert.Equal("ind", IndicatorEvent.TypeId);
    [Fact] public void IndicatorMutationEvent_TypeId() => Assert.Equal("ind.mut", IndicatorMutationEvent.TypeId);
    [Fact] public void SignalEvent_TypeId() => Assert.Equal("sig", SignalEvent.TypeId);
    [Fact] public void RiskEvent_TypeId() => Assert.Equal("risk", RiskEvent.TypeId);
    [Fact] public void OrderPlaceEvent_TypeId() => Assert.Equal("ord.place", OrderPlaceEvent.TypeId);
    [Fact] public void OrderFillEvent_TypeId() => Assert.Equal("ord.fill", OrderFillEvent.TypeId);
    [Fact] public void OrderCancelEvent_TypeId() => Assert.Equal("ord.cancel", OrderCancelEvent.TypeId);
    [Fact] public void OrderRejectEvent_TypeId() => Assert.Equal("ord.reject", OrderRejectEvent.TypeId);
    [Fact] public void PositionEvent_TypeId() => Assert.Equal("pos", PositionEvent.TypeId);
    [Fact] public void RunStartEvent_TypeId() => Assert.Equal("run.start", RunStartEvent.TypeId);
    [Fact] public void RunEndEvent_TypeId() => Assert.Equal("run.end", RunEndEvent.TypeId);
    [Fact] public void ErrorEvent_TypeId() => Assert.Equal("err", ErrorEvent.TypeId);
    [Fact] public void WarningEvent_TypeId() => Assert.Equal("warn", WarningEvent.TypeId);

    [Fact] public void BarEvent_ExportMode() => Assert.Equal(ExportMode.Backtest, BarEvent.DefaultExportMode);
    [Fact] public void BarMutationEvent_ExportMode() => Assert.Equal(ExportMode.Backtest, BarMutationEvent.DefaultExportMode);
    [Fact] public void TickEvent_ExportMode() => Assert.Equal(ExportMode.Backtest, TickEvent.DefaultExportMode);
    [Fact] public void IndicatorEvent_ExportMode() => Assert.Equal(ExportMode.Backtest, IndicatorEvent.DefaultExportMode);
    [Fact] public void IndicatorMutationEvent_ExportMode() => Assert.Equal(ExportMode.Backtest, IndicatorMutationEvent.DefaultExportMode);

    [Fact]
    public void SignalEvent_ExportMode() =>
        Assert.Equal(ExportMode.Backtest | ExportMode.Live, SignalEvent.DefaultExportMode);

    [Fact]
    public void RiskEvent_ExportMode() =>
        Assert.Equal(ExportMode.Backtest | ExportMode.Live, RiskEvent.DefaultExportMode);

    [Fact]
    public void OrderFillEvent_ExportMode() =>
        Assert.Equal(ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live, OrderFillEvent.DefaultExportMode);

    [Fact]
    public void RunStartEvent_ExportMode() =>
        Assert.Equal(ExportMode.Backtest | ExportMode.Optimization | ExportMode.Live, RunStartEvent.DefaultExportMode);

    #endregion

    [Fact]
    public void BarEvent_Serializes_With_Correct_Envelope_And_Payload()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        bus.Emit(new BarEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));

        Assert.Single(sink.Received);
        var doc = JsonDocument.Parse(sink.Received[0]);
        var root = doc.RootElement;

        // Envelope fields
        Assert.True(root.TryGetProperty("ts", out var tsProp));
        Assert.True(root.TryGetProperty("sq", out var sqProp));
        Assert.True(root.TryGetProperty("_t", out var typeProp));
        Assert.True(root.TryGetProperty("src", out var srcProp));
        Assert.True(root.TryGetProperty("d", out var dProp));

        Assert.Equal(1, sqProp.GetInt64());
        Assert.Equal("bar", typeProp.GetString());
        Assert.Equal("engine", srcProp.GetString());

        // Payload fields
        Assert.Equal("BTCUSDT", dProp.GetProperty("assetName").GetString());
        Assert.Equal("1m", dProp.GetProperty("timeFrame").GetString());
        Assert.Equal(100, dProp.GetProperty("open").GetInt64());
        Assert.Equal(110, dProp.GetProperty("high").GetInt64());
        Assert.Equal(90, dProp.GetProperty("low").GetInt64());
        Assert.Equal(105, dProp.GetProperty("close").GetInt64());
        Assert.Equal(1000, dProp.GetProperty("volume").GetInt64());

        // JsonIgnore fields should NOT appear in payload
        Assert.False(dProp.TryGetProperty("timestamp", out _));
        Assert.False(dProp.TryGetProperty("source", out _));
        Assert.False(dProp.TryGetProperty("isExportable", out _));
    }

    [Fact]
    public void Enums_Serialize_As_CamelCase_Strings()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        bus.Emit(new OrderPlaceEvent(Ts, "engine", 42, "BTCUSDT", OrderSide.Buy, OrderType.StopLimit, 1.0m, 50000, 49000));

        var doc = JsonDocument.Parse(sink.Received[0]);
        var d = doc.RootElement.GetProperty("d");

        Assert.Equal("buy", d.GetProperty("side").GetString());
        Assert.Equal("stopLimit", d.GetProperty("type").GetString());
    }

    [Fact]
    public void Timestamp_Is_Iso8601_Utc()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        bus.Emit(new WarningEvent(Ts, "engine", "test"));

        var doc = JsonDocument.Parse(sink.Received[0]);
        var tsString = doc.RootElement.GetProperty("ts").GetString()!;

        // Should parse back to the same offset
        var parsed = DateTimeOffset.Parse(tsString);
        Assert.Equal(Ts, parsed);
        Assert.Contains("2025-06-01", tsString);
    }

    [Fact]
    public void Null_Optional_Fields_Omitted_From_Json()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        bus.Emit(new SignalEvent(Ts, "strategy", "Signal1", "BTCUSDT", "Long", 1.0m, null));

        var doc = JsonDocument.Parse(sink.Received[0]);
        var d = doc.RootElement.GetProperty("d");

        Assert.False(d.TryGetProperty("reason", out _));
    }

    [Fact]
    public void IndicatorEvent_Serializes_Measure_As_String()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        var values = new Dictionary<string, object?> { ["sma"] = 42.5, ["upper"] = null };
        bus.Emit(new IndicatorEvent(Ts, "engine", "BollingerBands", IndicatorMeasure.Price, values, true));

        var doc = JsonDocument.Parse(sink.Received[0]);
        var d = doc.RootElement.GetProperty("d");

        Assert.Equal("price", d.GetProperty("measure").GetString());
        Assert.Equal("BollingerBands", d.GetProperty("indicatorName").GetString());
    }
}
