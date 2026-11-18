using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace SptPrometheusExporter.Metrics;

/// <summary>
/// A Prometheus histogram family: per series, cumulative _bucket{le="..."} counts plus _sum and _count.
/// Thread-safe; each series is updated and read under its own lock so bucket counts, sum and count
/// always form a consistent snapshot.
/// </summary>
public sealed class Histogram : IMetricFamily
{
    /// <summary>Default latency buckets in seconds (Prometheus client defaults).</summary>
    public static readonly double[] DefaultSecondsBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private sealed class SeriesState(int bucketCount)
    {
        // Non-cumulative: index i counts observations in (bounds[i-1], bounds[i]]; the extra last slot is +Inf.
        public readonly long[] BucketCounts = new long[bucketCount + 1];
        public double Sum;
        public long Count;
    }

    private readonly record struct SeriesSnapshot(string Labels, long[] BucketCounts, double Sum, long Count);

    private readonly ConcurrentDictionary<string, SeriesState> _series = new();

    private readonly double[] _upperBounds;

    public Histogram(string name, string help, double[] upperBounds)
    {
        Name = name;
        Help = help;
        _upperBounds = upperBounds.Order().ToArray();
    }

    public string Name { get; }

    public string Help { get; }

    public void Observe(string labels, double value)
    {
        var bucketIndex = Array.FindIndex(_upperBounds, upperBound => value <= upperBound);
        if (bucketIndex < 0)
        {
            bucketIndex = _upperBounds.Length; // +Inf
        }

        var state = _series.GetOrAdd(labels, _ => new SeriesState(_upperBounds.Length));
        lock (state)
        {
            state.BucketCounts[bucketIndex]++;
            state.Sum += value;
            state.Count++;
        }
    }

    public void Render(StringBuilder builder)
    {
        builder.Append("# HELP ").Append(Name).Append(' ').AppendLine(Help);
        builder.Append("# TYPE ").Append(Name).AppendLine(" histogram");

        foreach (var series in Snapshot())
        {
            var labelPrefix = series.Labels.Length > 0 ? series.Labels + "," : string.Empty;
            long cumulativeCount = 0;

            for (var i = 0; i < series.BucketCounts.Length; i++)
            {
                cumulativeCount += series.BucketCounts[i];
                var upperBound = i < _upperBounds.Length ? FormatNumber(_upperBounds[i]) : "+Inf";

                builder.Append(Name).Append("_bucket{")
                    .Append(labelPrefix).Append("le=\"").Append(upperBound).Append("\"} ")
                    .Append(cumulativeCount.ToString(CultureInfo.InvariantCulture))
                    .AppendLine();
            }

            AppendSample(builder, "_sum", series.Labels, FormatNumber(series.Sum));
            AppendSample(builder, "_count", series.Labels, series.Count.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Copies every series under its lock first, so rendering never happens while a lock is held.</summary>
    private List<SeriesSnapshot> Snapshot()
    {
        var snapshot = new List<SeriesSnapshot>(_series.Count);
        foreach (var (labels, state) in _series)
        {
            lock (state)
            {
                snapshot.Add(new SeriesSnapshot(labels, (long[])state.BucketCounts.Clone(), state.Sum, state.Count));
            }
        }

        return snapshot;
    }

    private void AppendSample(StringBuilder builder, string suffix, string labels, string value)
    {
        builder.Append(Name).Append(suffix);
        if (labels.Length > 0)
        {
            builder.Append('{').Append(labels).Append('}');
        }

        builder.Append(' ').Append(value).AppendLine();
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
