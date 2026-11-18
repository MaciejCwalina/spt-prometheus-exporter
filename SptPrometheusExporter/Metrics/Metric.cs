using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace SptPrometheusExporter.Metrics;

/// <summary>
/// A single Prometheus metric family (counter or gauge) with zero or more label dimensions.
/// Thread-safe; all series live in one concurrent dictionary keyed by their rendered label string,
/// and every read or write of a series value happens under that series' lock.
/// </summary>
public sealed class Metric
{
    private sealed class SeriesBox
    {
        public double Value;
    }

    private readonly ConcurrentDictionary<string, SeriesBox> _series = new();

    public Metric(string name, string help, string type)
    {
        Name = name;
        Help = help;
        Type = type;
    }

    public string Name { get; }

    public string Help { get; }

    public string Type { get; }

    /// <summary>Increments (or decrements) the given series by delta.</summary>
    public void Add(string labels, double delta)
    {
        var box = _series.GetOrAdd(labels, _ => new SeriesBox());
        lock (box)
        {
            box.Value += delta;
        }
    }

    /// <summary>Sets the absolute value of the given series (gauge semantics).</summary>
    public void Set(string labels, double value)
    {
        var box = _series.GetOrAdd(labels, _ => new SeriesBox());
        lock (box)
        {
            box.Value = value;
        }
    }

    /// <summary>
    /// Adds delta to the given series but never lets it drop below floor.
    /// Used for gauges rebuilt from up/down deltas, where missed increments could otherwise push it negative.
    /// </summary>
    public void AddWithFloor(string labels, double delta, double floor)
    {
        var box = _series.GetOrAdd(labels, _ => new SeriesBox());
        lock (box)
        {
            box.Value = Math.Max(floor, box.Value + delta);
        }
    }

    public double Get(string labels)
    {
        if (!_series.TryGetValue(labels, out var box))
        {
            return 0;
        }

        lock (box)
        {
            return box.Value;
        }
    }

    /// <summary>Copies every series value under its lock, so callers never iterate while a lock is held.</summary>
    public IEnumerable<KeyValuePair<string, double>> Snapshot()
    {
        var snapshot = new List<KeyValuePair<string, double>>(_series.Count);
        foreach (var (labels, box) in _series)
        {
            lock (box)
            {
                snapshot.Add(new KeyValuePair<string, double>(labels, box.Value));
            }
        }

        return snapshot;
    }
}

/// <summary>Builds Prometheus label strings from name/value pairs, escaping values per the exposition format.</summary>
public static class LabelFormat
{
    public const string None = "";

    public static string Build(params (string Name, string Value)[] labels)
    {
        if (labels.Length == 0)
        {
            return None;
        }

        return string.Join(",", labels.Select(label => $"{label.Name}=\"{Escape(label.Value)}\""));
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    }

    /// <summary>Renders one full metric family in the Prometheus text exposition format.</summary>
    public static void RenderFamily(StringBuilder builder, Metric metric)
    {
        builder.Append("# HELP ").Append(metric.Name).Append(' ').AppendLine(metric.Help);
        builder.Append("# TYPE ").Append(metric.Name).Append(' ').AppendLine(metric.Type);
        foreach (var (labels, value) in metric.Snapshot())
        {
            builder.Append(metric.Name);
            if (labels.Length > 0)
            {
                builder.Append('{').Append(labels).Append('}');
            }

            builder.Append(' ')
                .Append(value.ToString("R", CultureInfo.InvariantCulture))
                .AppendLine();
        }
    }
}
