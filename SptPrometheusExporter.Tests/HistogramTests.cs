using System.Globalization;
using System.Text;
using SptPrometheusExporter.Metrics;

namespace SptPrometheusExporter.Tests;

public class HistogramTests
{
    private const string Name = "test_duration_seconds";

    private static readonly double[] SmallBounds = [0.1, 0.5, 1];

    private static Histogram CreateHistogram(double[]? upperBounds = null)
    {
        return new Histogram(Name, "A test histogram.", upperBounds ?? SmallBounds);
    }

    private static string[] RenderLines(Histogram histogram)
    {
        var builder = new StringBuilder();
        histogram.Render(builder);
        return builder.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
    }

    /// <summary>Parses every sample line of one series into "suffix{labels}" -> value.</summary>
    private static Dictionary<string, double> Samples(string[] lines)
    {
        return lines
            .Where(line => !line.StartsWith('#'))
            .Select(line => line.Split(' '))
            .ToDictionary(parts => parts[0], parts => double.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static double Bucket(Dictionary<string, double> samples, string upperBound, string labelPrefix = "")
    {
        return samples[$"{Name}_bucket{{{labelPrefix}le=\"{upperBound}\"}}"];
    }

    [Fact]
    public void Render_EmptyHistogram_EmitsOnlyHelpAndType()
    {
        Assert.Equal(
            [$"# HELP {Name} A test histogram.", $"# TYPE {Name} histogram"],
            RenderLines(CreateHistogram()));
    }

    [Fact]
    public void Render_UnlabeledSeries_EmitsBucketsSumAndCountInOrder()
    {
        var histogram = CreateHistogram();
        histogram.Observe(LabelFormat.None, 0.0625);
        histogram.Observe(LabelFormat.None, 0.25);
        histogram.Observe(LabelFormat.None, 2);

        Assert.Equal(
            [
                $"# HELP {Name} A test histogram.",
                $"# TYPE {Name} histogram",
                $"{Name}_bucket{{le=\"0.1\"}} 1",
                $"{Name}_bucket{{le=\"0.5\"}} 2",
                $"{Name}_bucket{{le=\"1\"}} 2",
                $"{Name}_bucket{{le=\"+Inf\"}} 3",
                $"{Name}_sum 2.3125",
                $"{Name}_count 3",
            ],
            RenderLines(histogram));
    }

    [Fact]
    public void Render_LabeledSeries_PutsLeAfterSeriesLabels()
    {
        var histogram = CreateHistogram();
        var labels = LabelFormat.Build(("method", "GET"), ("route", "/client/game/start"));
        histogram.Observe(labels, 0.2);

        var lines = RenderLines(histogram);

        Assert.Contains($"{Name}_bucket{{method=\"GET\",route=\"/client/game/start\",le=\"0.1\"}} 0", lines);
        Assert.Contains($"{Name}_bucket{{method=\"GET\",route=\"/client/game/start\",le=\"0.5\"}} 1", lines);
        Assert.Contains($"{Name}_bucket{{method=\"GET\",route=\"/client/game/start\",le=\"+Inf\"}} 1", lines);
        Assert.Contains($"{Name}_sum{{method=\"GET\",route=\"/client/game/start\"}} 0.2", lines);
        Assert.Contains($"{Name}_count{{method=\"GET\",route=\"/client/game/start\"}} 1", lines);
    }

    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(0.5, "0.5")]
    [InlineData(1.0, "1")]
    public void Observe_ValueExactlyOnUpperBound_IsCountedInThatBucket(double value, string expectedBucket)
    {
        // Prometheus buckets are "less than or equal": le="0.5" includes an observation of exactly 0.5.
        var histogram = CreateHistogram();
        histogram.Observe(LabelFormat.None, value);

        var samples = Samples(RenderLines(histogram));
        var boundTexts = new[] { "0.1", "0.5", "1" };
        var expectedIndex = Array.IndexOf(boundTexts, expectedBucket);

        for (var boundIndex = 0; boundIndex < boundTexts.Length; boundIndex++)
        {
            var expectedCount = boundIndex >= expectedIndex ? 1 : 0;
            Assert.Equal(expectedCount, Bucket(samples, boundTexts[boundIndex]));
        }
    }

    [Fact]
    public void Observe_ValueJustAboveUpperBound_GoesToNextBucket()
    {
        var histogram = CreateHistogram();
        histogram.Observe(LabelFormat.None, Math.BitIncrement(0.5));

        var samples = Samples(RenderLines(histogram));

        Assert.Equal(0, Bucket(samples, "0.5"));
        Assert.Equal(1, Bucket(samples, "1"));
    }

    [Fact]
    public void Observe_ValueAboveLargestBound_OnlyCountsInInfBucket()
    {
        var histogram = CreateHistogram();
        histogram.Observe(LabelFormat.None, 1000);

        var samples = Samples(RenderLines(histogram));

        Assert.Equal(0, Bucket(samples, "1"));
        Assert.Equal(1, Bucket(samples, "+Inf"));
        Assert.Equal(1, samples[$"{Name}_count"]);
    }

    [Fact]
    public void Observe_ZeroAndNegativeValues_LandInFirstBucket()
    {
        var histogram = CreateHistogram();
        histogram.Observe(LabelFormat.None, 0);
        histogram.Observe(LabelFormat.None, -1);

        var samples = Samples(RenderLines(histogram));

        Assert.Equal(2, Bucket(samples, "0.1"));
        Assert.Equal(-1, samples[$"{Name}_sum"]);
    }

    [Fact]
    public void Render_BucketCountsAreCumulativeAndInfEqualsCount()
    {
        var histogram = CreateHistogram(Histogram.DefaultSecondsBuckets);
        var random = new Random(12345);
        var observations = Enumerable.Range(0, 1000).Select(_ => random.NextDouble() * 12).ToArray();
        foreach (var observation in observations)
        {
            histogram.Observe(LabelFormat.None, observation);
        }

        var lines = RenderLines(histogram);
        var samples = Samples(lines);
        var bucketLines = lines.Where(line => line.StartsWith($"{Name}_bucket")).ToArray();

        Assert.Equal(Histogram.DefaultSecondsBuckets.Length + 1, bucketLines.Length);

        double previousCount = 0;
        foreach (var upperBound in Histogram.DefaultSecondsBuckets)
        {
            var bucketCount = Bucket(samples, upperBound.ToString("R", CultureInfo.InvariantCulture));
            Assert.True(bucketCount >= previousCount, $"bucket le={upperBound} decreased");
            Assert.Equal(observations.Count(observation => observation <= upperBound), bucketCount);
            previousCount = bucketCount;
        }

        Assert.Equal(observations.Length, Bucket(samples, "+Inf"));
        Assert.Equal(observations.Length, samples[$"{Name}_count"]);
        Assert.Equal(observations.Sum(), samples[$"{Name}_sum"], 1e-9);
    }

    [Fact]
    public void Constructor_SortsUnorderedBounds()
    {
        var histogram = CreateHistogram([1, 0.1, 0.5]);
        histogram.Observe(LabelFormat.None, 0.3);

        var bucketLines = RenderLines(histogram).Where(line => line.StartsWith($"{Name}_bucket")).ToArray();

        Assert.Equal(
            [
                $"{Name}_bucket{{le=\"0.1\"}} 0",
                $"{Name}_bucket{{le=\"0.5\"}} 1",
                $"{Name}_bucket{{le=\"1\"}} 1",
                $"{Name}_bucket{{le=\"+Inf\"}} 1",
            ],
            bucketLines);
    }

    [Fact]
    public void Constructor_DoesNotAliasCallerArray()
    {
        var bounds = new double[] { 0.1, 0.5, 1 };
        var histogram = CreateHistogram(bounds);
        bounds[0] = 100;

        histogram.Observe(LabelFormat.None, 0.05);

        Assert.Contains($"{Name}_bucket{{le=\"0.1\"}} 1", RenderLines(histogram));
    }

    [Fact]
    public void Render_SeparateSeriesAreTrackedIndependently()
    {
        var histogram = CreateHistogram();
        var getLabels = LabelFormat.Build(("method", "GET"));
        var postLabels = LabelFormat.Build(("method", "POST"));
        histogram.Observe(getLabels, 0.05);
        histogram.Observe(postLabels, 0.7);
        histogram.Observe(postLabels, 0.8);

        var samples = Samples(RenderLines(histogram));

        Assert.Equal(1, samples[$"{Name}_count{{method=\"GET\"}}"]);
        Assert.Equal(2, samples[$"{Name}_count{{method=\"POST\"}}"]);
        Assert.Equal(1, Bucket(samples, "0.1", "method=\"GET\","));
        Assert.Equal(0, Bucket(samples, "0.5", "method=\"POST\","));
        Assert.Equal(2, Bucket(samples, "1", "method=\"POST\","));
    }

    [Fact]
    public void Render_UsesInvariantCultureUnderCommaDecimalCulture()
    {
        var histogram = CreateHistogram();

        string[] lines;
        using (new CultureScope("pl-PL"))
        {
            histogram.Observe(LabelFormat.None, 0.25);
            histogram.Observe(LabelFormat.None, 0.5);
            lines = RenderLines(histogram);
        }

        Assert.Contains($"{Name}_bucket{{le=\"0.1\"}} 0", lines);
        Assert.Contains($"{Name}_bucket{{le=\"0.5\"}} 2", lines);
        Assert.Contains($"{Name}_sum 0.75", lines);
        Assert.DoesNotContain(lines, line => line.Contains("0,"));
    }

    [Fact]
    public async Task Observe_FromManyThreads_CountsEveryObservationExactly()
    {
        var histogram = CreateHistogram();
        const int TaskCount = 16;
        const int ObservationsPerTask = 10_000;

        var tasks = Enumerable.Range(0, TaskCount).Select(taskIndex => Task.Run(() =>
        {
            for (var observationIndex = 0; observationIndex < ObservationsPerTask; observationIndex++)
            {
                // Quarter-second steps are exact in binary, so the expected sum is exact too.
                histogram.Observe(LabelFormat.None, (observationIndex % 4) * 0.25);
            }
        }, TestContext.Current.CancellationToken));

        // Render concurrently with the writers to exercise the snapshot path under contention.
        var renderTask = Task.Run(() =>
        {
            for (var renderIndex = 0; renderIndex < 50; renderIndex++)
            {
                var samples = Samples(RenderLines(histogram));
                if (samples.TryGetValue($"{Name}_count", out var count))
                {
                    Assert.Equal(count, Bucket(samples, "+Inf"));
                }
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(tasks.Append(renderTask));

        const int TotalObservations = TaskCount * ObservationsPerTask;
        var finalSamples = Samples(RenderLines(histogram));

        Assert.Equal(TotalObservations, finalSamples[$"{Name}_count"]);
        Assert.Equal(TotalObservations, Bucket(finalSamples, "+Inf"));
        // 0 falls in le=0.1; 0.25 and 0.5 (exactly on the bound) in le=0.5; 0.75 in le=1.
        Assert.Equal(TotalObservations / 4, Bucket(finalSamples, "0.1"));
        Assert.Equal(TotalObservations * 3 / 4, Bucket(finalSamples, "0.5"));
        Assert.Equal(TotalObservations, Bucket(finalSamples, "1"));
        Assert.Equal(TotalObservations / 4 * (0 + 0.25 + 0.5 + 0.75), finalSamples[$"{Name}_sum"]);
    }

    [Fact]
    public void DefaultSecondsBuckets_MatchPrometheusClientDefaults()
    {
        Assert.Equal([0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10], Histogram.DefaultSecondsBuckets);
    }
}
