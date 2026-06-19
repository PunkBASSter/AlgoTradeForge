using IBApi;

namespace IbPoc;

internal static class Contracts
{
    public static Contract Aapl() => new()
    {
        Symbol = "AAPL",
        SecType = "STK",
        Exchange = "SMART",
        PrimaryExch = "NASDAQ",
        Currency = "USD",
    };

    public static async Task<int> ResolveAsync(IbConnection conn, DemoWrapper wrapper, Contract contract, int reqId)
    {
        var task = wrapper.ResolveConIdAsync(reqId);
        conn.Client.reqContractDetails(reqId, contract);
        var conId = await task.WaitAsync(TimeSpan.FromSeconds(15));
        contract.ConId = conId;
        return conId;
    }
}
