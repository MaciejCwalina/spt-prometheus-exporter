using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Utils;
using SptPrometheusExporter.Metrics;

namespace SptPrometheusExporter.Metrics;

/// <summary>
/// Singleton owning every metric exposed by this mod.
/// Intentionally has no dependencies (no logger) so it can be injected into the logger decorator
/// without creating a DI cycle.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class PrometheusMetrics
{
    private static readonly DateTime StartTime = DateTime.UtcNow;

    // --- Server lifecycle -----------------------------------------------------

    public Metric UptimeSeconds { get; } = new("spt_uptime_seconds", "SPT server uptime in seconds.", "gauge");

    public Metric VersionInfo { get; } = new("spt_version_info", "SPT server version information (value is always 1).", "gauge");

    // --- Players / websocket --------------------------------------------------

    /// <summary>
    /// Open notifier websocket connections. This counts connections, not distinct players:
    /// a player that reconnects before the old socket closes is briefly counted twice.
    /// </summary>
    public Metric WebSocketConnections { get; } = new(
        "spt_websocket_connections",
        "Number of websocket connections currently open on the player notifier endpoint (connections, not distinct players).",
        "gauge"
    );

    public Metric WsConnectionsTotal { get; } = new(
        "spt_ws_connections_total",
        "Total websocket connections opened by players. Has a session label only when perSessionMetrics is enabled.",
        "counter"
    );

    public Metric WsDisconnectionsTotal { get; } = new(
        "spt_ws_disconnections_total",
        "Total websocket connections closed by players. Has a session label only when perSessionMetrics is enabled.",
        "counter"
    );

    public Metric WsPacketsReceivedTotal { get; } = new(
        "spt_ws_packets_received_total",
        "Total websocket messages received from players. Has a session label only when perSessionMetrics is enabled.",
        "counter"
    );

    public Metric WsUpgradedConnectionsTotal { get; } = new(
        "spt_ws_upgraded_connections_total",
        "Total HTTP connections upgraded to websockets by Kestrel.",
        "counter"
    );

    /// <summary>
    /// Info metric mapping session ids to player usernames (value always 1).
    /// Used with group_left(username) joins so dashboards can show names instead of ids.
    /// Only published when perSessionMetrics is enabled (it exports usernames). Series are not
    /// removed on disconnect (Metric has no delete), so they accumulate until the server restarts.
    /// </summary>
    public Metric SessionInfo { get; } = new(
        "spt_session_info",
        "Mapping from session id to player username (value is always 1). Opt-in via perSessionMetrics; series persist until server restart.",
        "gauge"
    );

    // --- Exceptions / logging -------------------------------------------------

    public Metric ExceptionsTotal { get; } = new(
        "spt_exceptions_total",
        "Exceptions observed through the SPT logging pipeline (every log call that carried an exception), grouped by .NET exception type.",
        "counter"
    );

    public Metric DotnetExceptionsTotal { get; } = new(
        "dotnet_exceptions_total",
        "All exceptions thrown in the process as reported by the .NET runtime meter, grouped by exception type.",
        "counter"
    );

    public Metric LogEntriesTotal { get; } = new(
        "spt_log_entries_total",
        "Log entries written by the server, grouped by level.",
        "counter"
    );

    public Metric UnhandledExceptionsTotal { get; } = new(
        "spt_unhandled_exceptions_total",
        "Process-level unhandled exceptions that bypassed normal logging, grouped by source and exception type.",
        "counter"
    );

    // --- HTTP (from ASP.NET Core meters) ----------------------------------------

    public Metric HttpRequestsTotal { get; } = new(
        "spt_http_requests_total",
        "HTTP requests processed by the server, grouped by method, protocol, status code and route.",
        "counter"
    );

    public Metric HttpRequestDurationSum { get; } = new(
        "spt_http_request_duration_seconds_sum",
        "Cumulative time spent processing HTTP requests in seconds.",
        "counter"
    );

    public Metric HttpRequestDurationCount { get; } = new(
        "spt_http_request_duration_seconds_count",
        "Number of HTTP requests with a measured latency.",
        "counter"
    );

    // --- Kestrel ----------------------------------------------------------------

    public Metric KestrelActiveConnections { get; } = new(
        "kestrel_active_connections",
        "Kestrel connections currently active.",
        "gauge"
    );

    public Metric KestrelQueuedRequests { get; } = new(
        "kestrel_queued_requests",
        "Kestrel requests waiting in the queue.",
        "gauge"
    );

    // --- Process / runtime (from .NET meters + sampling) ------------------------

    public Metric ProcessMemoryBytes { get; } = new("process_memory_bytes", "Process working set size in bytes.", "gauge");

    public Metric ProcessThreads { get; } = new("process_threads", "Process thread count.", "gauge");

    public Metric ProcessCpuSecondsTotal { get; } = new("process_cpu_seconds_total", "Process CPU time in seconds.", "counter");

    public Metric ThreadPoolThreads { get; } = new("dotnet_thread_pool_threads", ".NET thread pool thread count.", "gauge");

    public Metric GcCollectionsTotal { get; } = new(
        "dotnet_gc_collections_total",
        "Garbage collection counts by generation.",
        "counter"
    );

    public Metric GcPauseTimeSecondsTotal { get; } = new(
        "dotnet_gc_pause_time_seconds_total",
        "Time spent in garbage collection pauses in seconds.",
        "counter"
    );

    private readonly Metric[] _all;

    public PrometheusMetrics()
    {
        _all =
        [
            UptimeSeconds,
            VersionInfo,
            WebSocketConnections,
            WsConnectionsTotal,
            WsDisconnectionsTotal,
            WsPacketsReceivedTotal,
            WsUpgradedConnectionsTotal,
            SessionInfo,
            ExceptionsTotal,
            DotnetExceptionsTotal,
            LogEntriesTotal,
            UnhandledExceptionsTotal,
            HttpRequestsTotal,
            HttpRequestDurationSum,
            HttpRequestDurationCount,
            KestrelActiveConnections,
            KestrelQueuedRequests,
            ProcessMemoryBytes,
            ProcessThreads,
            ProcessCpuSecondsTotal,
            ThreadPoolThreads,
            GcCollectionsTotal,
            GcPauseTimeSecondsTotal,
        ];
    }

    public void SetVersion()
    {
        VersionInfo.Set(LabelFormat.Build(("version", ProgramStatics.SPT_VERSION().ToString())), 1);
    }

    public void UpdateUptime()
    {
        UptimeSeconds.Set(LabelFormat.None, (DateTime.UtcNow - StartTime).TotalSeconds);
    }

    public string Render()
    {
        var builder = new StringBuilder(64 * 1024);
        foreach (var metric in _all)
        {
            LabelFormat.RenderFamily(builder, metric);
        }

        return builder.ToString();
    }
}
