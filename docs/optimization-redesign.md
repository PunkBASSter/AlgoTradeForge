Optimization redesign:

Currently in optimization we have a params axis for data subscriptions mixed with other params' axes. Currently it means that I'm not able to see any optimization results until ALL of the param combinations including ALL data subscriptions are processed. But I would like to split the optimization process by the individual data subscription. So each data subscription result would be accessible immediately after its processing individually regardless of other subscriptions processed or not.

Genetic optimization during results filtering can leave just 1 or 2 data subs among the survivors - which is reasonable to leave the strongest survivors but hits cross-asset visibility and comparison opportunities.

So I'd like to change the optimization flow to actually move away DataSub from optimization axes and launch each individual optimization run for each data subscription (including genetic mode - each individual genetic optimization should be done on each asset data subscription (or set of subscriptions per multi-asset strategy) and selection/crossover should happen between results per subscription set).

DSS stands for data subscription set

Now
Common optimization for DSS_AXIS * ALL_OTHER_AXES -> Top Fitness results across all DSS

Updated
Per DSS AXIS dedicated optimization for ALL_OTHER_AXES -> Separate results per each DSS, Top by Fitness within each group.

For optimization trial and backtest table record in FE add a CSV string of parameter Key:Value pairs for each trial, call this new column Params and make it last.
Make it sortable by all metric columns.
Make each trial ID clickable to open a backtest launching side panel (even on the current optimization run page) with the same parameters as in the trial pre-populated in the JSON editor, only left to click start button.
All optimizations per DSS need to be tracked separately in the table and the optimizations launched at once per a list of DSS should be visually grouped (e.g. by a single optimization group id - probably the new entity meaning the grouping of optimization sets launched at once)

A new tab with cross-DSS groups table with sortable data grid by each metric (ON FE) - contains all trials of the optimizations grouping by list of DSS.
Table needs to have the same capabilities as for trials and backtests, initially grouped by DSS but can be sortable by any other metric.

In `+New Optimization` json editor we need to add another standalone JSON editor specifically for data subscriptions above the params JSON editor and make it in a collapsible component for easy hiding.
For `+New Backtest` (non-debug mode) there needs to be an option to launch the same backtest parameters on the selected data subscriptions, so the subscriptions set list should be available in debug start menu as well.

Don't hesitate to delete any existing data from the DB to avoid making complicated and long-running data migrations. Drop to change the schema is OK.

Validation should be launchable per optimization DSS group but should be referencing the initial optimization group of listed DSS (new entity for optimization launch grouping).

On the existing validations tab there should be `DSS optimization -> Validation` 1-1 item but referencing the DSS list based optimization launch grouping.
There needs to be a new tab for cross-DSS validation withing each grouping with the same capabilities as for cross-DSS optimization tab described above.