# Long running operations flow fix

There was a big mistake in API design in regards to Run Backtest and Run Optimization command handlers:
Both are for potentially long running operations (backtest for minutes, optimization for hours) that should me done in the background. But the handlers are designed in a synchronous manner.

It's required to redesign the existent handlers to save starting data for a new run in MemoryCache (dotnet abstraction, pick the best fit), start the job/worker/task and immediately return the run ID (maybe with details: period length in main data sub bars number for backtest and total count of combinations for optimization).

When processing is complete, the complete run details are saved to the persistent storage.
During optimization trials are saved on each run and saved to memory cache on iteration completion, so that they can be found by the RunId (TBD: composite key, research possibilities).

Other endpoints for both backtest and optimization need to be exposed for completion check using RunId. Frontend can poll these endpoints so that they retrieve the status or number of actually processed bars or param combinations for BT and OPT respectively, or if a run is completed, include the results (same as existing handlers have now).

FE needs to poll the check endpoints every 5 seconds for backtest and every 30 seconds for optimization (Or it's better to check via websockets, or something else with push model).
FE displays progress in raw numbers: "Processed  X / Total (Bars|Combinations)" 

Decision Q&A:
Q: How to actually run a long task and still be able to access (to cancel and get a status field value), and keep the hot loop fast performing?
A:Optimization: TaskFactory+LongRunning with cancellation token, generate RunId, storee number of iteration to Cache by RunId (once per N iterations or in the end, expiry 15 min), invalidate it after successful saving to DB.

Q: Best run data storage (local single node for now)?
A: MemoryCache abstraction from Dotnet with one node impl now, should cover cache change, e.g. to distributed if needed later.

Q: Should I persist the run record from the start, or update it continuously as the run goes on?
A: Probably no, run data itself should not be that valuable, we can always rerun. No continuation on failure is planned (at least for now).