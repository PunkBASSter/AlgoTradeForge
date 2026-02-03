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
- History loading job
- History partitioning
- History streaming loader, replace custom csv parser
- Backtest running job with Start/Stop/Pause/Resume/Next Bar/Next Trade API
- Migrate ZZ strategies with indicator basics
- Add event streaming from backtest to DB in debug mode
- Connect web UI to backtest jobs and history loading jobs