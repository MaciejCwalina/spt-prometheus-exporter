using System.Text;
using SptPrometheusExporter.Metrics;

namespace SptPrometheusExporter.Tests;

public class MetricTests
{
    private static Metric CreateGauge()
    {
        return new Metric("test_gauge", "A test gauge.", "gauge");
    }

    private static string Render(IMetricFamily family)
    {
        var builder = new StringBuilder();
        family.Render(builder);
        return builder.ToString();
    }

    private static string[] Lines(string rendered)
    {
        return rendered.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
    }

    [Fact]
    public void Get_UnknownSeries_ReturnsZero()
    {
        Assert.Equal(0, CreateGauge().Get(LabelFormat.None));
    }

    [Fact]
    public void Add_AccumulatesPositiveAndNegativeDeltas()
    {
        var metric = CreateGauge();

        metric.Add(LabelFormat.None, 3);
        metric.Add(LabelFormat.None, 2.5);
        metric.Add(LabelFormat.None, -1);

        Assert.Equal(4.5, metric.Get(LabelFormat.None));
    }

    [Fact]
    public void Set_ReplacesValue()
    {
        var metric = CreateGauge();

        metric.Add(LabelFormat.None, 10);
        metric.Set(LabelFormat.None, 2);

        Assert.Equal(2, metric.Get(LabelFormat.None));
    }

    [Fact]
    public void Series_AreIndependentPerLabelSet()
    {
        var metric = CreateGauge();
        var errorLabels = LabelFormat.Build(("level", "error"));
        var warningLabels = LabelFormat.Build(("level", "warning"));

        metric.Add(errorLabels, 1);
        metric.Add(warningLabels, 5);
        metric.Add(errorLabels, 1);

        Assert.Equal(2, metric.Get(errorLabels));
        Assert.Equal(5, metric.Get(warningLabels));
        Assert.Equal(0, metric.Get(LabelFormat.None));
    }

    [Fact]
    public void AddWithFloor_ClampsAtFloor()
    {
        var metric = CreateGauge();

        metric.AddWithFloor(LabelFormat.None, -1, 0);
        Assert.Equal(0, metric.Get(LabelFormat.None));

        metric.AddWithFloor(LabelFormat.None, 3, 0);
        metric.AddWithFloor(LabelFormat.None, -5, 0);
        Assert.Equal(0, metric.Get(LabelFormat.None));

        // Once clamped, later increments start from the floor rather than from the "true" negative value.
        metric.AddWithFloor(LabelFormat.None, 2, 0);
        Assert.Equal(2, metric.Get(LabelFormat.None));
    }

    [Fact]
    public void AddWithFloor_AboveFloor_BehavesLikeAdd()
    {
        var metric = CreateGauge();

        metric.AddWithFloor(LabelFormat.None, 5, 0);
        metric.AddWithFloor(LabelFormat.None, -2, 0);

        Assert.Equal(3, metric.Get(LabelFormat.None));
    }

    [Fact]
    public void Snapshot_ContainsEverySeriesWithCurrentValues()
    {
        var metric = CreateGauge();
        var firstLabels = LabelFormat.Build(("generation", "gen0"));
        var secondLabels = LabelFormat.Build(("generation", "gen1"));
        metric.Set(firstLabels, 7);
        metric.Set(secondLabels, 3);

        var snapshot = metric.Snapshot().ToDictionary(entry => entry.Key, entry => entry.Value);

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(7, snapshot[firstLabels]);
        Assert.Equal(3, snapshot[secondLabels]);
    }

    [Fact]
    public void Snapshot_IsACopyThatDoesNotSeeLaterWrites()
    {
        var metric = CreateGauge();
        metric.Set(LabelFormat.None, 1);

        var snapshot = metric.Snapshot().ToList();
        metric.Set(LabelFormat.None, 99);
        metric.Set(LabelFormat.Build(("extra", "series")), 1);

        Assert.Single(snapshot);
        Assert.Equal(1, snapshot[0].Value);
    }

    [Fact]
    public async Task Add_FromManyThreads_LosesNoIncrements()
    {
        var metric = new Metric("test_total", "A test counter.", "counter");
        const int TaskCount = 16;
        const int IncrementsPerTask = 10_000;

        var tasks = Enumerable.Range(0, TaskCount).Select(taskIndex => Task.Run(() =>
        {
            var labels = LabelFormat.Build(("worker", (taskIndex % 2).ToString()));
            for (var increment = 0; increment < IncrementsPerTask; increment++)
            {
                metric.Add(labels, 1);
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        var total = metric.Snapshot().Sum(entry => entry.Value);
        Assert.Equal(TaskCount * IncrementsPerTask, total);
    }

    [Fact]
    public void Render_EmptyFamily_EmitsOnlyHelpAndType()
    {
        var metric = new Metric("spt_uptime_seconds", "SPT server uptime in seconds.", "gauge");

        Assert.Equal(
            ["# HELP spt_uptime_seconds SPT server uptime in seconds.", "# TYPE spt_uptime_seconds gauge"],
            Lines(Render(metric)));
    }

    [Fact]
    public void Render_UnlabeledSeries_HasNoBraces()
    {
        var metric = new Metric("process_threads", "Process thread count.", "gauge");
        metric.Set(LabelFormat.None, 42);

        Assert.Equal(
            ["# HELP process_threads Process thread count.", "# TYPE process_threads gauge", "process_threads 42"],
            Lines(Render(metric)));
    }

    [Fact]
    public void Render_LabeledSeries_WrapsLabelsInBraces()
    {
        var metric = new Metric("spt_log_entries_total", "Log entries.", "counter");
        metric.Add(LabelFormat.Build(("level", "error")), 3);

        var lines = Lines(Render(metric));

        Assert.Equal("# TYPE spt_log_entries_total counter", lines[1]);
        Assert.Equal("spt_log_entries_total{level=\"error\"} 3", lines[2]);
    }

    [Fact]
    public void Render_EscapedLabelValue_AppearsEscapedInOutput()
    {
        var metric = new Metric("spt_session_info", "Session info.", "gauge");
        metric.Set(LabelFormat.Build(("session", "abc"), ("username", "Bad\"Name\\\n")), 1);

        Assert.Contains("spt_session_info{session=\"abc\",username=\"Bad\\\"Name\\\\\\n\"} 1", Lines(Render(metric)));
    }

    [Fact]
    public void Render_MultipleSeries_EmitsOneLinePerSeriesAfterHeader()
    {
        var metric = new Metric("dotnet_gc_collections_total", "GC counts.", "counter");
        metric.Set(LabelFormat.Build(("generation", "gen0")), 10);
        metric.Set(LabelFormat.Build(("generation", "gen1")), 2);
        metric.Set(LabelFormat.Build(("generation", "gen2")), 1);

        var lines = Lines(Render(metric));

        Assert.Equal(5, lines.Length);
        Assert.StartsWith("# HELP ", lines[0]);
        Assert.StartsWith("# TYPE ", lines[1]);
        Assert.Contains("dotnet_gc_collections_total{generation=\"gen0\"} 10", lines);
        Assert.Contains("dotnet_gc_collections_total{generation=\"gen1\"} 2", lines);
        Assert.Contains("dotnet_gc_collections_total{generation=\"gen2\"} 1", lines);
    }

    [Theory]
    [InlineData(0.5, "0.5")]
    [InlineData(1234567.25, "1234567.25")]
    [InlineData(-3.75, "-3.75")]
    [InlineData(0.1, "0.1")]
    public void Render_UsesInvariantCultureUnderCommaDecimalCulture(double value, string expectedText)
    {
        var metric = CreateGauge();
        metric.Set(LabelFormat.None, value);

        string rendered;
        using (new CultureScope("pl-PL"))
        {
            rendered = Render(metric);
        }

        Assert.Contains($"test_gauge {expectedText}", Lines(rendered));
    }

    [Fact]
    public void Render_RoundTripsDoublePrecision()
    {
        var metric = CreateGauge();
        var value = 1.0 / 3.0;
        metric.Set(LabelFormat.None, value);

        var sampleLine = Lines(Render(metric))[2];
        var renderedValue = double.Parse(sampleLine.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(value, renderedValue);
    }

    [Fact]
    public void Render_UsesLineFeedLineEndings()
    {
        // The exposition format requires '\n' on every platform, never Environment.NewLine (CRLF on Windows).
        var metric = new Metric("test_total", "A test counter.", "counter");
        metric.Add(LabelFormat.Build(("type", "x")), 1);

        var rendered = Render(metric);

        Assert.EndsWith("\n", rendered);
        Assert.DoesNotContain("\r", rendered);
    }
}
