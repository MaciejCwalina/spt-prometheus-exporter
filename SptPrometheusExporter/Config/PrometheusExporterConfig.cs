using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Web.Models.Configs;
using SPTarkov.Server.Web.Services;

namespace SptPrometheusExporter.Config;

[Injectable(InjectionType.Singleton)]
public class PrometheusExporterConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 9464;

    [JsonPropertyName("bindAddress")]
    public string BindAddress { get; set; } = "0.0.0.0";

    [JsonPropertyName("sampleIntervalSeconds")]
    public int SampleIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Opt-in. When false (default) websocket counters are exported as server-wide totals with no
    /// session label and no usernames are exported. When true, spt_ws_connections_total,
    /// spt_ws_disconnections_total and spt_ws_packets_received_total get a "session" label and
    /// spt_session_info{session,username} is published. This exports player usernames and creates
    /// series per session id that are kept until the server restarts (unbounded cardinality).
    /// </summary>
    [JsonPropertyName("perSessionMetrics")]
    public bool PerSessionMetrics { get; set; } = false;
}

[Injectable(InjectionType.Singleton)]
public class PrometheusExporterConfigEditorProvider(PrometheusExporterConfig config) : IConfigEditorConfigProvider
{
    public IEnumerable<ConfigEditorConfigRegistration> GetConfigs()
    {
        yield return ConfigEditorConfigRegistration.Create(
            "com.maciej.spt-prometheus-exporter",
            "Prometheus Exporter",
            config,
            Path.Combine("user", "mods", "SptPrometheusExporter", "config.json")
        );
    }
}
