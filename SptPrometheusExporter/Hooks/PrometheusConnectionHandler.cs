using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers.Ws;
using SptPrometheusExporter.Config;
using SptPrometheusExporter.Metrics;

namespace SptPrometheusExporter.Hooks;

/// <summary>
/// Observes the player websocket lifecycle. The server invokes every registered
/// <see cref="IWebSocketConnectionHandler"/> whose hook URL matches the request path, so this
/// handler runs alongside the built-in one on the same "/notifierServer/getwebsocket/" endpoint
/// without modifying any server code.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class PrometheusConnectionHandler(
    PrometheusMetrics metrics,
    ProfileHelper profileHelper,
    PrometheusExporterConfig config
) : IWebSocketConnectionHandler
{
    /// <summary>Open connections keyed by "sessionId:connectionReference".</summary>
    private readonly ConcurrentDictionary<string, byte> _openConnections = new();

    public string GetHookUrl()
    {
        return "/notifierServer/getwebsocket/";
    }

    public string GetSocketId()
    {
        return "SPT Prometheus Exporter";
    }

    public Task OnConnectionAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var sessionId = GetSessionId(context);
        // Adjust the gauge only when the dictionary actually changed. Metric.Add is atomic, so
        // concurrent connects/closes cannot overwrite each other with a stale count.
        if (_openConnections.TryAdd(MakeKey(sessionId, sessionIdContext), 0))
        {
            metrics.WebSocketConnections.Add(LabelFormat.None, 1);
        }

        if (config.PerSessionMetrics)
        {
            // Opt-in only: publish the session -> username mapping so dashboards can show names instead of ids.
            metrics.SessionInfo.Set(LabelFormat.Build(("session", sessionId), ("username", ResolveUsername(sessionId))), 1);
        }

        metrics.WsConnectionsTotal.Add(SessionLabels(sessionId), 1);

        return Task.CompletedTask;
    }

    private string ResolveUsername(string sessionId)
    {
        try
        {
            var profile = profileHelper.GetFullProfile(new MongoId(sessionId));

            return string.IsNullOrWhiteSpace(profile.ProfileInfo?.Username) ? sessionId : profile.ProfileInfo.Username;
        }
        catch (Exception)
        {
            // Unknown session (no profile yet) - fall back to the raw id.
            return sessionId;
        }
    }

    public Task OnMessageAsync(byte[] rawData, WebSocketMessageType messageType, WebSocket ws, HttpContext context)
    {
        var sessionId = GetSessionId(context);
        metrics.WsPacketsReceivedTotal.Add(SessionLabels(sessionId), 1);

        return Task.CompletedTask;
    }

    public Task OnCloseAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var sessionId = GetSessionId(context);
        if (_openConnections.TryRemove(MakeKey(sessionId, sessionIdContext), out _))
        {
            metrics.WebSocketConnections.Add(LabelFormat.None, -1);
        }

        metrics.WsDisconnectionsTotal.Add(SessionLabels(sessionId), 1);

        return Task.CompletedTask;
    }

    /// <summary>Per-session label only when explicitly enabled; otherwise a single aggregate series (bounded cardinality).</summary>
    private string SessionLabels(string sessionId)
    {
        return config.PerSessionMetrics ? LabelFormat.Build(("session", sessionId)) : LabelFormat.None;
    }

    private static string MakeKey(string sessionId, string sessionIdContext)
    {
        return $"{sessionId}:{sessionIdContext}";
    }

    /// <summary>The session id is the last segment of the websocket request path (same convention as the built-in handler).</summary>
    private static string GetSessionId(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var lastSlash = path.LastIndexOf('/');

        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }
}
