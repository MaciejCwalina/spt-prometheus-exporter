using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace SptPrometheusExporter;

public sealed class PrometheusExporterModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.maciej.spt-prometheus-exporter";

    public string Name { get; init; } = "spt-prometheus-exporter";

    public string Author { get; init; } = "maciej";

    public List<string>? Contributors { get; init; }

    public Version Version { get; init; } = new("1.0.0");

    public Range SptVersion { get; init; } = new("~4.1.0");

    public bool HasPrepatcher { get; init; } = false;

    public List<string>? Incompatibilities { get; init; }

    public Dictionary<string, Range>? ModDependencies { get; init; }

    public string? Url { get; init; } = "https://github.com/SP-Tushonka/server-csharp";

    public string License { get; init; } = "MIT";
}
