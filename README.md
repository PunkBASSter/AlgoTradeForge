# AlgoTradeForge
Fast track from ideas to live trading

# TODO:
- Domain model and app - design history data feed for the strategy

BacktestEngine needs
- IBarSource
- IBarStrategy
- BacktestOptions

Ассет передаём в бэктест енджин как обязательный параметер для рана, стратегия получает только события.
Бэктест енджин кормит стратегию событиями из истории
История в бэктест енджине берется из IBarSource
Но другие параметры - нет, как передать параметры стратегии?
Для параметров стратегии сделать базовый тип (рабочий ассет, тф, репортинг тф), остальное - специфические параметры для конкретной стратегии
Сейчас: передается инстанс IBarStrategy - он уже может содержать конкретные параметры стратегии.
Откуда брать их? Из StrategyFactory, а там откуда? Дефолтные можно хардкодить. Сейчас они передаются через словарь в фабрику, берутся из ран бэктест команды.

1. Create WebApi (with minimal API) and Application (with UseCases invoking Domain logic) projects (incomplete)
2. Analyze existing Debug Mode, what can be done better if re-created from scratch


TODO:
- DataSource implemented + integrated with launcher and strategy resolving+subscribing to data
- Strategy has Asset+Tf params passed to data source
- Backtest running job with Start/Stop/Pause/Resume/Next Bar/Next Trade API
- Add reporting timeframe, add TimeSeries resample with debug feature
- Ensure BacktestEngine tracks and optimally fills multiple Orders
- Strategy must track entry orders and exit orders (multi SL/TP) on its own (migrate from SS.AB)
- History loading job
- Migrate ZZ strategies with indicator basics
- Add event streaming from backtest to DB in debug mode
- Connect web UI to backtest jobs and history loading jobs


## Reconsidered Backtest Engine flow:
1. Receives the strategy instance with parameters including data subscriptions (Asset+Tf).
```
public class StrategyParamsBase
{
    public IList<DataSubscription> DataSubscriptions { get; init; } = [];
}
```
2. Loads historical data for the strategy's subscriptions from IHistoryRepository (TODO: design this interface and its implementation, uses existing history query). For each subscription, for the requested period, loads bars and resamples them to the requested timeframe if needed, packs to TimeSeries and feeds to the strategy.
3. Backtest engine finds the smallest bar timestamp across all subscriptions and starts feeding the strategy with bars in chronological order, simulating real-time data feed. For each new bar, it triggers the strategy's logic to process the new data and generate signals/orders. Subscription events are generated in the same order as they would be in real-time, but with respect to the order in subscriptions (e.g., if two subscriptions have bars at the same timestamp, they are fed in the order of subscriptions).
3. Strategy receives bar updates from data sources as events, processes them. TODO: get rid of returned StrategyAction, it can do nothing.
4. Backtest engine listens to an in-memory order queue (TBD in BacktestEngine) where the strategy places its orders. For each new order, it simulates order execution based on the historical data and fills the order accordingly, recording them in its own special registry and generating fill events. If a current timeframe bar crosses the pending order price only (no SL/TP hit), we open at the order price. If the same bar also crosses the stop loss or take profit price, we first try to match it to specific cases where the outcome is clear, then we try to dig deeper into how the bar is formed on the lowest available timeframe (e.g., 1m or ticks if available) to determine the exact sequence of events and fill the order accordingly. If the order is filled, we generate a fill event and update the strategy's position. (This option is configurable in BacktestOptions, e.g., "UseDetailedExecutionLogic" or something like that). If lower timeframe data is not available, we assume the worst case scenario for the strategy.
Special cases for bars that cross the pending order price and SL/TP price:
- BUY STOP/LIMIT with SL not touched but OrderPrice and TP in bar range -> Order filled and TP filled -> Profit.
- BUY STOP/LIMIT with TP not touched but OrderPrice and SL in bar range -> Order filled and SL filled -> Loss.
- SELL STOP/LIMIT with SL not touched and OrderPrice and TP in bar range -> Order filled and TP filled -> Profit.
- SELL STOP/LIMIT with TP not touched but OrderPrice and SL in bar range -> Order filled and SL filled -> Loss.
**RISK: when we make our tester aware of PendingOrder SL and TP levels, not all production brokers/exchanges provide the same, so we the strategy may fail in prod.** Solution: develop a strategy module for tracking the orders with SL and multiple TPs on its own. BacktestEngine will be aware of SL/TP levels for checking fills, but will separately generate events for the strategy to track them on its own. This way, we can have the same logic in backtest and production, and the strategy will be able to track its orders and positions on its own without relying on the broker/exchange features. The Module will be additionally integrated with exact exchange/broker adapter.