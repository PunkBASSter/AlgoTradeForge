using AlgoTradeForge.Live.Relay;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class RelayFormatTests
{
    [Fact]
    public void Constants_MatchWireSpec()
    {
        Assert.Equal(64, RelayFormat.HeaderSize);
        Assert.Equal(40, RelayFormat.FrameSize);
        Assert.Equal(1, RelayFormat.CurrentVersion);
        Assert.True(RelayFormat.Magic.SequenceEqual("ATFT"u8));
    }
}
