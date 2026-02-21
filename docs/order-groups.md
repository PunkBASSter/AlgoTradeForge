# Order groups

For easier analysis of orders executed it's required to introduce order groups - some identifier common for a group of orders: e.g. entry, SL, TP1, TP2.
- From strategy there should be a separate module with configurable concurrent groups. The module is responsible for matching received order IDs to a specific order group for further management.
- Group ID will be also propagated to backtester BUT ONLY FOR BETTER EVENT LOGS AS OPTIONAL PARAM, NOT AFFECTING ANYTHING (to avoid confusions in Live mode)
- Groups should be exportable via debug event buses
- FE Debug should use order groups to connect Open and Close prices for orders with a dashed line.