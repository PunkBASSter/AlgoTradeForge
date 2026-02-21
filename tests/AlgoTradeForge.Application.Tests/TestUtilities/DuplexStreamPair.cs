using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace AlgoTradeForge.Application.Tests.TestUtilities;

/// <summary>
/// Creates paired in-memory streams and WebSockets for testing.
/// Each stream writes to the partner's read buffer.
/// </summary>
internal static class DuplexStreamPair
{
    public static (Stream A, Stream B) Create()
    {
        var a = new DuplexStream();
        var b = new DuplexStream();
        a.Partner = b;
        b.Partner = a;
        return (a, b);
    }

    public static (WebSocket Server, WebSocket Client) CreateLinkedWebSockets()
    {
        var (stream1, stream2) = Create();
        var server = WebSocket.CreateFromStream(stream1, new WebSocketCreationOptions { IsServer = true });
        var client = WebSocket.CreateFromStream(stream2, new WebSocketCreationOptions { IsServer = false });
        return (server, client);
    }

    private sealed class DuplexStream : Stream
    {
        private readonly SemaphoreSlim _dataAvailable = new(0);
        private readonly ConcurrentQueue<byte[]> _buffer = new();
        private byte[]? _current;
        private int _currentOffset;

        public DuplexStream? Partner { get; set; }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var data = new byte[count];
            Buffer.BlockCopy(buffer, offset, data, 0, count);
            Partner!._buffer.Enqueue(data);
            Partner._dataAvailable.Release();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var data = buffer.ToArray();
            Partner!._buffer.Enqueue(data);
            Partner._dataAvailable.Release();
            return ValueTask.CompletedTask;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_current is null || _currentOffset >= _current.Length)
                await _dataAvailable.WaitAsync(ct);
            return CopyFromBuffer(buffer.Span);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_current is null || _currentOffset >= _current.Length)
                _dataAvailable.Wait();
            return CopyFromBuffer(buffer.AsSpan(offset, count));
        }

        private int CopyFromBuffer(Span<byte> destination)
        {
            if (_current is null || _currentOffset >= _current.Length)
            {
                if (!_buffer.TryDequeue(out _current))
                    return 0;
                _currentOffset = 0;
            }

            var available = _current.Length - _currentOffset;
            var toCopy = Math.Min(available, destination.Length);
            _current.AsSpan(_currentOffset, toCopy).CopyTo(destination);
            _currentOffset += toCopy;
            return toCopy;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _dataAvailable.Release(); // Unblock any pending reads
            base.Dispose(disposing);
        }
    }
}
