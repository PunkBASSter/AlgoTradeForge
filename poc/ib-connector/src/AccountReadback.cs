namespace IbPoc;

internal static class AccountReadback
{
    public static async Task DumpAsync(IbConnection conn, DemoWrapper wrapper)
    {
        const int summaryReqId = 9001;
        conn.Client.reqPositions();
        conn.Client.reqAccountSummary(summaryReqId, "All", "NetLiquidation,TotalCashValue,BuyingPower");
        await Task.Delay(TimeSpan.FromSeconds(5));
        conn.Client.cancelAccountSummary(summaryReqId);
        conn.Client.cancelPositions();
    }
}
