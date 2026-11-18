using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SptPrometheusExporter.Config;
using SptPrometheusExporter.Metrics;

namespace SptPrometheusExporter.Service;

/// <summary>
/// Hosted service that serves the Prometheus exposition endpoint and feeds runtime metrics:
/// ASP.NET Core request counters/latency, Kestrel connection stats, .NET exception/GC/CPU
/// meters (via MeterListener) plus a periodic sampler for uptime and thread count.
/// </summary>
[Injectable(InjectionType.HostedService)]
public sealed class PrometheusExporterService(
    PrometheusMetrics metrics,
    PrometheusExporterConfig config,
    ISptLogger<PrometheusExporterService> logger
) : BackgroundService
{
    private const string MetricsContentType = "text/plain; version=0.0.4; charset=utf-8";

    private static readonly HashSet<string> TrackedMeters =
    [
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "System.Runtime",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        metrics.SetVersion();
        metrics.UpdateUptime();

        if (!config.Enabled)
        {
            logger.Info("Prometheus exporter is disabled in config, skipping listener startup.");
            return;
        }

        var port = ResolvePort();
        var listener = new HttpListener();
        listener.Prefixes.Add(BuildPrefix(config.BindAddress, port));

        try
        {
            listener.Start();
        }
        catch (HttpListenerException e)
        {
            logger.Error($"Prometheus exporter failed to bind http://{config.BindAddress}:{port} - {e.Message}");
            listener.Close();
            return;
        }

        logger.Info($"Prometheus exporter listening on http://{config.BindAddress}:{port}/metrics");

        using var meterListener = CreateMeterListener(metrics);
        var samplerTask = SampleProcessMetricsAsync(metrics, meterListener, TimeSpan.FromSeconds(config.SampleIntervalSeconds), stoppingToken);

        // GetContextAsync() takes no token, so stopping the listener is what unblocks the accept loop on shutdown.
        var stopRegistration = stoppingToken.Register(listener.Stop);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext httpContext;
                try
                {
                    httpContext = await listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        logger.Error("Prometheus exporter listener stopped unexpectedly", exception);
                    }

                    break;
                }

                // Serve off the accept loop so one slow client can't stall other scrapes.
                // HandleRequestAsync observes all of its own exceptions.
                _ = Task.Run(() => HandleRequestAsync(httpContext, metrics));
            }
        }
        finally
        {
            // Dispose the registration first (waits for a running callback) so Stop() never races Close().
            stopRegistration.Dispose();
            listener.Close();
            await samplerTask;
        }
    }

    private int ResolvePort()
    {
        var environmentPort = Environment.GetEnvironmentVariable("SPT_PROMETHEUS_PORT");

        if (int.TryParse(environmentPort, out var parsedPort) && parsedPort > 0 && parsedPort < 65536)
        {
            return parsedPort;
        }

        return config.Port;
    }

    private static string BuildPrefix(string bindAddress, int port)
    {
        // HttpListener prefixes accept "*" for any address, not "0.0.0.0".
        var host = string.Equals(bindAddress, "0.0.0.0", StringComparison.Ordinal) ? "*" : bindAddress;

        return $"http://{host}:{port}/";
    }

    private async Task HandleRequestAsync(HttpListenerContext httpContext, PrometheusMetrics currentMetrics)
    {
        var response = httpContext.Response;
        var path = httpContext.Request.Url?.AbsolutePath ?? "/";
        var succeeded = false;

        try
        {
            switch (path)
            {
                case "/metrics" or "/health" when httpContext.Request.HttpMethod != "GET":
                    response.AddHeader("Allow", "GET");
                    await WriteResponseAsync(response, HttpStatusCode.MethodNotAllowed, "text/plain", "method not allowed");
                    break;

                case "/metrics":
                    await WriteResponseAsync(response, HttpStatusCode.OK, MetricsContentType, currentMetrics.Render());
                    break;

                case "/health":
                    await WriteResponseAsync(response, HttpStatusCode.OK, "text/plain", "ok");
                    break;

                default:
                    await WriteResponseAsync(response, HttpStatusCode.NotFound, "text/plain", "not found");
                    break;
            }

            succeeded = true;
        }
        catch (Exception exception)
        {
            logger.Warning($"Prometheus exporter failed to serve {httpContext.Request.HttpMethod} {path}", exception);
            succeeded = await TryWriteServerErrorAsync(response);
        }
        finally
        {
            CloseResponse(response, succeeded);
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode, string contentType, string body)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>Returns a 500 if nothing has been sent yet; false when headers are already out (or the client is gone).</summary>
    private static async Task<bool> TryWriteServerErrorAsync(HttpListenerResponse response)
    {
        try
        {
            // Throws InvalidOperationException once headers have been sent.
            await WriteResponseAsync(response, HttpStatusCode.InternalServerError, "text/plain", "internal server error");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Closes a completed response, or aborts the connection if it ended up half-written.</summary>
    private static void CloseResponse(HttpListenerResponse response, bool completed)
    {
        try
        {
            if (completed)
            {
                response.Close();
                return;
            }
        }
        catch (Exception)
        {
            // Fall through to Abort() so the connection is still released.
        }

        response.Abort();
    }

    /// <summary>Subscribes to ASP.NET Core and .NET runtime meters and routes their instruments into our metrics.</summary>
    private static MeterListener CreateMeterListener(PrometheusMetrics currentMetrics)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (TrackedMeters.Contains(instrument.Meter.Name))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => RecordMeasurement(currentMetrics, instrument, tags, value));
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => RecordMeasurement(currentMetrics, instrument, tags, value));
        listener.Start();

        return listener;
    }

    private static void RecordMeasurement(
        PrometheusMetrics currentMetrics,
        Instrument instrument,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        double value
    )
    {
        switch (instrument.Name)
        {
            // One measurement per completed HTTP request; the histogram sample is the duration.
            case "http.server.request.duration":
                var requestLabels = BuildRequestLabels(tags);
                currentMetrics.HttpRequestDuration.Observe(requestLabels, value);
                currentMetrics.HttpRequestsTotal.Add(requestLabels, 1);
                break;

            case "dotnet.exceptions":
                currentMetrics.DotnetExceptionsTotal.Add(
                    LabelFormat.Build(("type", ReadTag(tags, "error.type") ?? "unknown")),
                    value
                );
                break;

            // Observable counters report the cumulative total on every observation, so Set (not Add) them.
            case "dotnet.gc.collections":
                currentMetrics.GcCollectionsTotal.Set(
                    LabelFormat.Build(("generation", ReadTag(tags, "gc.heap.generation") ?? "unknown")),
                    value
                );
                break;

            case "dotnet.gc.pause.time":
                currentMetrics.GcPauseTimeSecondsTotal.Set(LabelFormat.None, value);
                break;

            case "dotnet.process.memory.working_set":
                currentMetrics.ProcessResidentMemoryBytes.Set(LabelFormat.None, value);
                break;

            // dotnet.process.cpu.time is deliberately not used: it reports one series per cpu.mode
            // (user/system), while process_cpu_seconds_total is their sum. The sampler sets it instead.

            case "dotnet.thread_pool.thread.count":
                currentMetrics.ThreadPoolThreads.Set(LabelFormat.None, value);
                break;

            // These UpDownCounters publish deltas (+/- changes), and their current value cannot be read,
            // so the gauge is rebuilt from deltas seen since this listener started. A connection opened
            // before that and closed later would push it below the real value (even negative), so clamp
            // at 0: it can undercount briefly but never goes negative, and self-corrects once idle.
            case "kestrel.active_connections":
                currentMetrics.KestrelActiveConnections.AddWithFloor(LabelFormat.None, value, 0);
                break;

            case "kestrel.queued_requests":
                currentMetrics.KestrelQueuedRequests.AddWithFloor(LabelFormat.None, value, 0);
                break;

            // Also an UpDownCounter (connections currently upgraded): +1 on upgrade, -1 when the socket
            // closes. Only the increments are counted, so the counter is the total number of upgrades.
            case "kestrel.upgraded_connections":
                if (value > 0)
                {
                    currentMetrics.WsUpgradedConnectionsTotal.Add(LabelFormat.None, value);
                }

                break;
        }
    }

    private static string BuildRequestLabels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var method = ReadTag(tags, "http.request.method") ?? "unknown";
        var protocol = ReadTag(tags, "network.protocol.version") ?? "unknown";
        var statusCode = ReadTag(tags, "http.response.status_code") ?? "unknown";
        var route = ReadTag(tags, "http.route") ?? "unknown";

        // http.route (not raw path) is used on purpose: it keeps label cardinality bounded.
        return LabelFormat.Build(
            ("method", method),
            ("protocol", protocol),
            ("status_code", statusCode),
            ("route", route)
        );
    }

    private static string? ReadTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string tagName)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, tagName, StringComparison.Ordinal))
            {
                return Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static async Task SampleProcessMetricsAsync(
        PrometheusMetrics currentMetrics,
        MeterListener meterListener,
        TimeSpan interval,
        CancellationToken cancellationToken
    )
    {
        using var process = Process.GetCurrentProcess();
        using var timer = new PeriodicTimer(interval);

        void SampleOnce()
        {
            currentMetrics.UpdateUptime();

            // Process caches its info after the first read; Refresh() makes Threads report the live count.
            process.Refresh();
            currentMetrics.ProcessThreads.Set(LabelFormat.None, process.Threads.Count);

            // User + system CPU time, matching the standard process_cpu_seconds_total semantics.
            currentMetrics.ProcessCpuSecondsTotal.Set(LabelFormat.None, Environment.CpuUsage.TotalTime.TotalSeconds);

            // Observable instruments (gauges and cumulative counters) only report when asked.
            meterListener.RecordObservableInstruments();
        }

        try
        {
            // Sample once up front so the first scrape does not see zeros for a whole interval.
            SampleOnce();

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                SampleOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }
}
