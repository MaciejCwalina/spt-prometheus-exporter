using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace SptPrometheusExporter.Metrics;

/// <summary>
/// A single Prometheus metric family (counter or gauge) with zero or more label dimensions.
/// Thread-safe; all series live in one concurrent dictionary keyed by their rendered label string.
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

    public double Get(string labels)
    {
        return _series.TryGetValue(labels, out var box) ? box.Value : 0;
    }

    public IEnumerable<KeyValuePair<string, double>> Snapshot()
    {
        foreach (var (labels, box) in _series)
        {
            lock (box)
            {
                yield return new KeyValuePair<string, double>(labels, box.Value);
            }
        }
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
