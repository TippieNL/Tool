namespace SysMon.Core.History;

/// <summary>
/// A fixed-capacity circular buffer of samples. Adding a sample never allocates, which is what
/// lets the graphs run indefinitely without producing GC pressure.
///
/// Samples are <see cref="float"/> because the graphs only need display precision, and
/// <see cref="float.NaN"/> encodes a gap (sensor unavailable for that tick) so that the renderer
/// can break the line instead of drawing a misleading drop to zero.
///
/// Three threads touch a series: the monitoring loop adds, the render thread copies out, and the
/// UI thread resizes when the history duration changes. Every operation therefore takes a lock.
/// It is uncontended in practice (a handful of samples per second against a render pass), and the
/// alternative — a resize racing an add — indexes past the new buffer and throws.
/// </summary>
public sealed class HistorySeries
{
    private readonly Lock _gate = new();
    private float[] _buffer;
    private int _head;

    public HistorySeries(string id, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Id = id;
        _buffer = new float[capacity];
    }

    public string Id { get; }

    public int Capacity
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Length;
            }
        }
    }

    /// <summary>Number of samples currently held, at most <see cref="Capacity"/>.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    private int _count;

    /// <summary>Most recent sample, or null when empty or the newest sample is a gap.</summary>
    public double? Latest
    {
        get
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    return null;
                }

                var value = _buffer[(_head - 1 + _buffer.Length) % _buffer.Length];
                return float.IsNaN(value) ? null : value;
            }
        }
    }

    /// <summary>Adds a sample. <c>null</c> is stored as a gap.</summary>
    public void Add(double? value)
    {
        lock (_gate)
        {
            _buffer[_head] = value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
                ? float.NaN
                : (float)value.Value;

            _head = (_head + 1) % _buffer.Length;

            if (_count < _buffer.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>
    /// Copies samples oldest-first into <paramref name="destination"/> and returns how many were
    /// written. The renderer owns the destination array, so reading history allocates nothing.
    /// </summary>
    public int CopyTo(Span<float> destination)
    {
        lock (_gate)
        {
            var count = Math.Min(_count, destination.Length);
            if (count == 0)
            {
                return 0;
            }

            // Oldest sample sits `_count` slots behind the head.
            var start = (_head - _count + _buffer.Length) % _buffer.Length;

            // Skip the oldest samples when the destination cannot hold them all.
            start = (start + (_count - count)) % _buffer.Length;

            var firstRun = Math.Min(count, _buffer.Length - start);
            _buffer.AsSpan(start, firstRun).CopyTo(destination);

            if (firstRun < count)
            {
                _buffer.AsSpan(0, count - firstRun).CopyTo(destination[firstRun..]);
            }

            return count;
        }
    }

    /// <summary>
    /// Highest non-gap sample, or null when the series holds no real values. Used to auto-scale
    /// graphs whose range is not fixed, such as network throughput.
    /// </summary>
    public double? Max()
    {
        lock (_gate)
        {
            double? max = null;
            var start = (_head - _count + _buffer.Length) % _buffer.Length;

            for (var i = 0; i < _count; i++)
            {
                var value = _buffer[(start + i) % _buffer.Length];
                if (!float.IsNaN(value) && (max is null || value > max))
                {
                    max = value;
                }
            }

            return max;
        }
    }

    /// <summary>
    /// Changes capacity, keeping the most recent samples that still fit. Called when the user
    /// changes the graph history duration.
    /// </summary>
    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        lock (_gate)
        {
            if (capacity == _buffer.Length)
            {
                return;
            }

            var keep = Math.Min(_count, capacity);
            var replacement = new float[capacity];

            if (keep > 0)
            {
                // Take the newest `keep` samples.
                var start = (_head - keep + _buffer.Length) % _buffer.Length;
                var firstRun = Math.Min(keep, _buffer.Length - start);
                _buffer.AsSpan(start, firstRun).CopyTo(replacement);

                if (firstRun < keep)
                {
                    _buffer.AsSpan(0, keep - firstRun).CopyTo(replacement.AsSpan(firstRun));
                }
            }

            _buffer = replacement;
            _count = keep;
            _head = keep % capacity;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _count = 0;
            _head = 0;
        }
    }
}
