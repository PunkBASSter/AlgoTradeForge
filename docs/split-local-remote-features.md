# Split Local and Remote Features
Currently, the strategy framework, backtest engine, optimizer, and related tools are designed to be used only locally, on a single machine. The current data storage is based on SQLite. I want to split the local and remote features of the platform, so that the strategy development and testing can be done locally, while the live trading and backtest/optimization data storage can be done remotely, on a server.
Technical implementation should be to have a single frontend for all, a remote API for live trading and backtest/optimization data storage, and a local API for strategy development and testing. It means creating a new AlgoTradeForge.Local.WebAPI project to handle debug, backtest, optimization that save the results locally and sync them with the remote server on demand.
It means we need an authentication and authorization system for the remote API, and a way to sync the local and remote data. We also need to ensure that the local and remote APIs have the same contracts and data models, so that the frontend can switch between them seamlessly. Registration feature is also required for it, for now just an email and password, no confirmation, no password recovery, etc. We can add them later if needed.
Also we need API key mechanism.
We need to add a dedicated models validation layer probably using FluentValidation or a similar library, injected into the API framework pipeline.

Task prioritization:
1. Create the new AlgoTradeForge.Local.WebAPI project and set up the basic structure and dependencies.
2. Migrate the remote API data layer to PostgreSQL from SQLite, and ensure that the local API can still use SQLite for local data storage.
3. Implement the authentication and authorization system for the remote API, and the API key mechanism.
4. Implement the data synchronization mechanism between the local and remote APIs, ensuring that the data models and contracts are consistent.
5. Add the models validation layer to both APIs, and ensure that it is properly integrated into the API framework pipeline.
6. Update the frontend to support switching between the local and remote APIs, and ensure that the user experience is seamless when doing so.
7. Test the new architecture thoroughly, including unit tests, integration tests, and end-to-end tests, to ensure that all features work correctly and that there are no issues with data consistency or performance.
8. Update the documentation to reflect the new architecture and how to use the local and remote features of the platform.