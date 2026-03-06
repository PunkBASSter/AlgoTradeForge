# Live connector for Binance

Research on the APIs and the way to authenticate.
Create a domain class (designed as a lazy thread-safe singleton) connected to binance via its most recommended API (probably websockets).
Class receives strategy instances and info about their data subscriptions. Each data subscription is listening to relevant data, that is routed to the strategy instances via their event hooks.
The connector has (or is) an implementation of an IOrderContext passed to the relevant strategy hooks. Connector also routes relevant trade events to the strategy.
Make sure strategy OnTrade hooks receive IOrderContext as well.
Strategy live launch should be doable via UI. There should be an endpoint to setup the connector, receiving all the data. In UI should be the mocked data examples with all default settings real except the secrets. Suggest where to store the secrets for the local app for most balance of convenience and security.


Feedback about session page:
Now:
```
Market Data
Bid/Ask quotes will appear here when the session is connected to the exchange.

Recent Orders
Order history will appear here once the session places trades.

Account Funds
Account balance and equity will appear here when connected.
```
Can we fetch previous data and show it, then next to it show the data after session start.

Bid/Ask quotes will appear here when the session is connected to the exchange. - This is not changed, even after a candle strategy launched for M1 did not show anything after several minutes, even after page refresh