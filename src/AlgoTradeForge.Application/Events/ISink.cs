namespace AlgoTradeForge.Application.Events;

public interface ISink
{
    void Write(ReadOnlyMemory<byte> utf8Json);
}
