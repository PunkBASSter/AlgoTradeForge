# Strategy Framework

**Goal:** enhance the strategy code structure by decomposing it into modules with exact responsibilities, being able to reuse the existing modules as building blocks for strategies, being able to flexibly 

Make base strategy class do all the necessary infrastructure and initialization work under the hood to make inherited exact strategy classes more focused on trading logic, this can be achieved via Template Method design patter, exposing virtual/abstract Inner logic methods for descendants. 

Suggested modules:
- TradeRegistry for tracking orders, order groups, fills, positions, SL/TP, belonging of the orders/positions/order groups to the current strategy, proxying trade events, handling orders on reconnections during live sessions. The key is the convenient API to create an order group owned by the strategy and track it (with persistence when live), giving virtual isolated order management for the strategy trading on the account, so that it
- Context module for holding all available history data, some custom info for all modules, needs to be accessible by all of them. Maybe have an event-routing mechanism so that a module can publish an event, and others receive it. 
- ? Signal for sending a collection of orders to place
- ? Filter for evaluation which trade directions are possible at the moment
- Exit module for making decisions on positions closing
- MoneyManagement evaluates risk per deal, can adjust it according to the current context.


```
 See @src/AlgoTradeForge.Domain/Strategy/StrategyBase.cs @src/AlgoTradeForge.Domain/Indicators/Int64IndicatorBase.cs @src/AlgoTradeForge.Domain/Strategy/Modules/Filter/IFilterModule.cs  @src/AlgoTradeForge.Domain/Strategy/IOrderContext.cs and various factories. Make sure it's compatible with the existing optimization, backtesting and debug.
```

(?) Return type of Filter and Exit modules? Int [-100, 100] (?)