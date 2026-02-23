# API Tests

I need to create API tests in C# with xUnit and NSubstitute (use standard assertions without extra libs for assertions) you can use Fixture to create the data.
Containearize the API application and use TestContainers for launching it, hosting all the dependency services.
Create a new API test project to verify all API endpoints. Start with happy paths, then add negative tests. Use data driven TheoryData of xUnit where available.
For test run speed please set up container before all tests and tear down after. Exception: the tests requiring a complex cleanup.
Note that the API is still under construction, so its behavior may be not 100% correct, so some suspicious behavior should be reported and investigated separately before creating tests.

As the starting point create tests verifying the behavior of endpoints described in stills /backtest and /optimize - use data and the model strategy from there `[StrategyKey("ZigZagBreakout")]`. 

All endpoints should be covered. Update constitution that each API endpoint change needs to be reflected in API Test project as well.