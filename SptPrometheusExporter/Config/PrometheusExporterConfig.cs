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
