using System.Threading;

namespace Agent.Services;

public sealed class RuntimeStats
{
    private int _webSocketClients;
    private long _lastTickUnixMs;
    private readonly RollingAverage _collectionMs = new(60);

    public int WebSocketClients => Volatile.Read(ref _webSocketClients);

    public DateTimeOffset? LastTickTime
    {
        get
        {
            var lastTick = Interlocked.Read(ref _lastTickUnixMs);
            return lastTick == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(lastTick);
        }
    }

    public double? AverageCollectionMs => _collectionMs.GetAverage();

    public void ClientConnected()
    {
        Interlocked.Increment(ref _webSocketClients);
    }

    public void ClientDisconnected()
    {
        Interlocked.Decrement(ref _webSocketClients);
    }

    public void RecordTick(DateTimeOffset timestamp, double collectionMs)
    {
        Interlocked.Exchange(ref _lastTickUnixMs, timestamp.ToUnixTimeMilliseconds());
        _collectionMs.Add(collectionMs);
    }

    public InternalStatsSnapshot GetSnapshot()
    {
        return new InternalStatsSnapshot
        {
            WebSocketClients = WebSocketClients,
            LastTickTime = LastTickTime,
            AverageCollectionMs = AverageCollectionMs
        };
    }

    private sealed class RollingAverage
    {
        private readonly double[] _buffer;
        private int _index;
        private int _count;
        private double _sum;
        private readonly object _lock = new();

        public RollingAverage(int size)
        {
            _buffer = new double[Math.Max(1, size)];
        }

        public void Add(double value)
        {
            lock (_lock)
            {
                if (_count < _buffer.Length)
                {
                    _buffer[_index] = value;
                    _sum += value;
                    _count++;
                }
                else
                {
                    _sum -= _buffer[_index];
                    _buffer[_index] = value;
                    _sum += value;
                }

                _index++;
                if (_index >= _buffer.Length)
                {
                    _index = 0;
                }
            }
        }

        public double? GetAverage()
        {
            lock (_lock)
            {
                return _count == 0 ? null : _sum / _count;
            }
        }
    }
}

public sealed record InternalStatsSnapshot
{
    public int WebSocketClients { get; init; }
    public DateTimeOffset? LastTickTime { get; init; }
    public double? AverageCollectionMs { get; init; }
}
