namespace AlgoTradeForge.LiveHost.WebApi;

public sealed class RelayPumpOptions
{
    public string LocalRoot { get; set; } = "relay-segments";
    public string KeyPrefix { get; set; } = "live-md";
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan UploadInterval { get; set; } = TimeSpan.FromSeconds(60);
}
