namespace SysMon.Core.History;

/// <summary>
/// A fixed-capacity circular buffer of samples. Adding a sample never allocates, which is what
/// lets the graphs run indefinitely without producing GC pressure.
///
/// Samples are <see cref="float"/> because the graphs only need display precision, and
/// <see cref="float.NaN"/> encodes a gap (sensor unavailable for that tick) so that the renderer
/// can break the line instead of drawing a misleading drop to zero.
/// </summary>
public sealed class HistorySeries
{
    private float[] _buffer;
    private int _head;

    public HistorySeries(string id, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Id = id;
        _buffer = new float[capacity];
    }

    public string Id { get; }

    public int Capacity => _buffer.Length;

    /// <summary>Number of samples currently held, at most <see cref="Capacity"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Most recent sample, or null when empty or the newest sample is a gap.</summary>
    public double? Latest
    {
        get
        {
            if (Count == 0)
            {
                return null;
            }

            var value = _buffer[(_head - 1 + _buffer.Length) % _buffer.Length];
            return float.IsNaN(value) ? null : value;
        }
    }

    /// <summary>Adds a sample. <c>null</c> is stored as a gap.</summary>
    public void Add(double? value)
    {
        _buffer[_head] = value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
            ? float.NaN
            : (float)value.Value;

        _head = (_head + 1) % _buffer.Length;

        if (Count < _buffer.Length)
        {
            Count++;
        }
    }

    /// <summary>
    /// Copies samples oldest-first into <paramref name="destination"/> and returns how many were
    /// written. The renderer owns the destination array, so reading history allocates nothing.
    /// </summary>
    public int CopyTo(Span<float> destination)
    {
        var count = Math.Min(Count, destination.Length);
        if (count == 0)
        {
            return 0;
        }

        // Oldest sample sits `Count` slots behind the head.
        var start = (_head - Count + _buffer.Length) % _buffer.Length;

        // Skip the oldest samples when the destination cannot hold them all.
        start = (start + (Count - count)) % _buffer.Length;

        var firstRun = Math.Min(count, _buffer.Length - start);
        _buffer.AsSpan(start, firstRun).CopyTo(destination);

        if (firstRun < count)
        {
            _buffer.AsSpan(0, count - firstRun).CopyTo(destination[firstRun..]);
        }

        return count;
    }

    /// <summary>
    /// Highest non-gap sample, or null when the series holds no real values. Used to auto-scale
    /// graphs whose range is not fixed, such as network throughput.
    /// </summary>
    public double? Max()
    {
        double? max = null;
        var start = (_head - Count + _buffer.Length) % _buffer.Length;

        for (var i = 0; i < Count; i++)
        {
            var value = _buffer[(start + i) % _buffer.Length];
            if (!float.IsNaN(value) && (max is null || value > max))
            {
                max = value;
            }
        }

        return max;
    }

    /// <summary>
    /// Changes capacity, keeping the most recent samples that still fit. Called when the user
    /// changes the graph history duration.
    /// </summary>
    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        if (capacity == _buffer.Length)
        {
            return;
        }

        var keep = Math.Min(Count, capacity);
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
        Count = keep;
        _head = keep % capacity;
    }

    public void Clear()
    {
        Count = 0;
        _head = 0;
    }
}
