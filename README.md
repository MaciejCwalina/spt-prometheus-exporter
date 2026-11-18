# SPT Prometheus Exporter

[![CI](https://github.com/MaciejCwalina/spt-prometheus-exporter/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/MaciejCwalina/spt-prometheus-exporter/actions/workflows/ci.yml)

A server mod for [SPT](https://github.com/SP-Tushonka/server-csharp), an open-source single-player
game server, that exposes server observability metrics in the
[Prometheus exposition format](https://prometheus.io/docs/instrumenting/exposition_formats/).

No server code is modified — the mod hooks into existing extension points:

| Hook | What it observes |
|---|---|
| `IWebSocketConnectionHandler` (second registration on the notifier endpoint) | Player websocket connect/disconnect + every packet received from players |
| `ISptLogger<T>` decorator (registered after the built-in logger) | Every log entry by level, and every exception carried through the logging pipeline |
| `IOnLoad` hook | Process-level unhandled exceptions (`AppDomain`, unobserved tasks) |
| `MeterListener` on ASP.NET Core / Kestrel / .NET runtime meters | HTTP request counts + latency, Kestrel connection stats, .NET exception counter, GC, memory, thread pool |
| `BackgroundService` | Serves `/metrics` on its own port, samples uptime, threads and CPU time |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A checkout of the [server repository](https://github.com/SP-Tushonka/server-csharp) at `../server-csharp`
  (i.e. a sibling of this repo — `build.sh` clones it for you if missing)

## Building

```bash
./build.sh            # clones ../server-csharp if needed, builds, copies the DLL
```

`build.sh` reads two optional environment variables:

- `SERVER_REPO` — path to the server checkout (default: `../server-csharp` next to this repo)
- `CONFIGURATION` — `Debug` (default) or `Release`

The compiled mod ends up in the server's
`SPTushonka.Server/bin/<Configuration>/net10.0/user/mods/SptPrometheusExporter/` folder, which is where the
server picks mods up from. Start the server normally; you should see:

```
Mod: spt-prometheus-exporter version: 1.0.0 (...) loaded
Prometheus exporter listening on http://0.0.0.0:9464/metrics
```

### Building without the script

The project references the server's library projects through the `ServerRepoDir` MSBuild property,
which defaults to `../server-csharp` next to this repo. To build against a checkout somewhere else:

```bash
dotnet build SptPrometheusExporter/SptPrometheusExporter.csproj -p:ServerRepoDir=/absolute/path/to/server-csharp
```

Use an absolute path: a relative `ServerRepoDir` is resolved from the `SptPrometheusExporter/` project
folder, not from your current directory. Then copy `SptPrometheusExporter.dll` from
`SptPrometheusExporter/bin/<Configuration>/net10.0/` into the server's `user/mods/SptPrometheusExporter/`.

The mod only needs the server's C# projects, not its Git LFS data, so a shallow clone without LFS
(`GIT_LFS_SKIP_SMUDGE=1 git clone --depth 1 ...`, about 330 MB) is enough to compile it. Running the
server still needs the LFS files.

## Testing

```bash
dotnet test --project SptPrometheusExporter.Tests
```

The tests cover the metric primitives (`Metric`, `LabelFormat`, `Histogram`): label escaping, the
rendered exposition text, invariant-culture number formatting, histogram bucket semantics and
concurrent updates. The test project compiles those source files directly rather than referencing
the mod project, so it does not need the server checkout. It uses xUnit v3 on Microsoft.Testing.Platform
(selected in `global.json`), which is why `dotnet test` takes `--project`.

CI (`.github/workflows/ci.yml`) runs the tests and also builds the mod against a shallow, LFS-free
clone of the server repository's default branch.

## Configuration

Editable in the SPT config editor (appears as "Prometheus Exporter"), or directly in
`user/mods/SptPrometheusExporter/config.json`:

```json
{
  "enabled": true,
  "port": 9464,
  "bindAddress": "0.0.0.0",
  "sampleIntervalSeconds": 15,
  "perSessionMetrics": false
}
```

| Option | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Start the metrics listener. When `false` the mod loads but serves nothing. |
| `port` | `9464` | Listener port. Overridden by the `SPT_PROMETHEUS_PORT` environment variable if set. |
| `bindAddress` | `0.0.0.0` | Address to listen on; `0.0.0.0` means all interfaces. |
| `sampleIntervalSeconds` | `15` | How often uptime, thread count, CPU time and the .NET runtime gauges/counters (GC, memory, thread pool) are refreshed. |
| `perSessionMetrics` | `false` | Opt-in per-player metrics, see below. |

### `perSessionMetrics`

Off by default. When off, the websocket counters are single server-wide series with no labels and no
player data is exported.

When on:

- `spt_ws_connections_total`, `spt_ws_disconnections_total` and `spt_ws_packets_received_total` get a
  `session` label (the player's session id);
- `spt_session_info{session,username}` is published, which **exports player usernames**;
- every session id gets its own series, and those series are never removed — they accumulate until
  the server restarts.

Only enable it if you are fine with usernames ending up in Prometheus and the player count is small.

## Endpoints

| Path | Response |
|---|---|
| `GET /metrics` | Metrics in the text exposition format (`text/plain; version=0.0.4`) |
| `GET /health` | `ok` |
| any other method on those paths | `405 Method Not Allowed` with `Allow: GET` |
| any other path | `404 Not Found` |

Scrapes are served concurrently, off the accept loop.

## Metrics

Labels in *italics* only exist when `perSessionMetrics` is enabled.

### Server lifecycle
| Metric | Type | Labels | Description |
|---|---|---|---|
| `spt_uptime_seconds` | gauge | | Server uptime in seconds |
| `spt_version_info` | gauge | `version` | SPT server version (value always 1) |

There is no separate "server is up" metric — use the `up` series Prometheus records for every scrape.

### Players / websocket ("packet" layer)
| Metric | Type | Labels | Description |
|---|---|---|---|
| `spt_websocket_connections` | gauge | | Websocket connections currently open on the player notifier endpoint. Counts connections, not distinct players: a player who reconnects before the old socket closes is briefly counted twice |
| `spt_ws_connections_total` | counter | *`session`* | Websocket connections opened by players |
| `spt_ws_disconnections_total` | counter | *`session`* | Websocket connections closed by players |
| `spt_ws_packets_received_total` | counter | *`session`* | **Every message received from a player** |
| `spt_ws_upgraded_connections_total` | counter | | HTTP connections upgraded to websockets by Kestrel, since the exporter started |
| `spt_session_info` | gauge | *`session`*, *`username`* | Session id → player username mapping (value always 1); use with `group_left(username)` joins to show names in dashboards. Only published when `perSessionMetrics` is enabled |

**Packet loss semantics:** the EFT notifier protocol has no client acks, so true end-to-end
loss is not observable server-side. What this mod measures:
- received packet rate (`spt_ws_packets_received_total`) — server-wide by default, per player with
  `perSessionMetrics`; a stalled/dead client shows up as a flatlined rate;
- send-side delivery failures surface as `WebSocketException` in `spt_exceptions_total` /
  `dotnet_exceptions_total`;
- connection churn: `spt_ws_connections_total` and `spt_ws_disconnections_total` against the
  currently open `spt_websocket_connections`.

### Exceptions
| Metric | Type | Labels | Description |
|---|---|---|---|
| `spt_exceptions_total` | counter | `type` | Exceptions logged anywhere in the server (via the `ISptLogger<T>` decorator), by short .NET exception type name (e.g. `WebSocketException`) |
| `dotnet_exceptions_total` | counter | `type` | All exceptions thrown in the process (including caught ones), straight from the .NET runtime meter, by short exception type name |
| `spt_unhandled_exceptions_total` | counter | `source`, `type` | Exceptions that bypassed logging entirely; `source` is `appdomain` or `task_scheduler` |
| `spt_log_entries_total` | counter | `level` | Log volume by level, as lowercase .NET log level names (`debug`, `information`, `warning`, `error`, `critical`) |

### HTTP
| Metric | Type | Labels | Description |
|---|---|---|---|
| `spt_http_requests_total` | counter | `method`, `protocol`, `status_code`, `route` | Requests processed (route-based, bounded cardinality) |
| `spt_http_request_duration_seconds` | histogram | `method`, `protocol`, `status_code`, `route` | Request latency: `_bucket{le}`, `_sum`, `_count`; buckets 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 seconds plus `+Inf` |
| `kestrel_active_connections` | gauge | | Active Kestrel connections, see caveat below |
| `kestrel_queued_requests` | gauge | | Requests waiting in the Kestrel queue, see caveat below |

**Kestrel gauge caveat:** Kestrel only publishes these as up/down changes, so the exporter rebuilds
them from the changes it has seen since it started. Connections that were already open when the
exporter started are not included, and the gauges are clamped at 0 instead of going negative when
such a connection closes. They can undercount briefly and settle once the server is idle.

### Process / runtime
| Metric | Type | Labels | Description |
|---|---|---|---|
| `process_resident_memory_bytes` | gauge | | Resident memory (the .NET working set, which on Linux is the RSS) |
| `process_threads` | gauge | | Process thread count |
| `process_cpu_seconds_total` | counter | | Total user + system CPU time in seconds |
| `dotnet_thread_pool_threads` | gauge | | .NET thread pool thread count |
| `dotnet_gc_collections_total` | counter | `generation` | GC collections by generation (`gen0`, `gen1`, `gen2`) |
| `dotnet_gc_pause_time_seconds_total` | counter | | Time spent in GC pauses in seconds |

These are refreshed every `sampleIntervalSeconds`, not on each scrape.

### Renamed and removed metrics

| Old | Now |
|---|---|
| `spt_up` | removed — use Prometheus' own `up{job="spt"}` |
| `spt_players_connected` | `spt_websocket_connections` |
| `process_memory_bytes` | `process_resident_memory_bytes` |
| `{session}` label on the `spt_ws_*_total` counters, `spt_session_info` | only with `perSessionMetrics: true` |

## Prometheus scrape config

```yaml
scrape_configs:
  - job_name: spt
    static_configs:
      - targets: ["<server-ip>:9464"]
```

## Example queries and alerts

```promql
# Server (or the exporter) down
up{job="spt"} == 0

# Exception spike (any type)
sum(rate(dotnet_exceptions_total[5m])) > 1

# Players connected but nobody is sending packets (stalled clients)
spt_websocket_connections > 0 and on() sum(rate(spt_ws_packets_received_total[5m])) == 0

# A specific player stopped sending packets (requires perSessionMetrics)
rate(spt_ws_packets_received_total{session="..."}[5m]) == 0

# Average request latency
rate(spt_http_request_duration_seconds_sum[5m]) / rate(spt_http_request_duration_seconds_count[5m]) > 1

# p95 request latency per route
histogram_quantile(0.95, sum by (le, route) (rate(spt_http_request_duration_seconds_bucket[5m])))

# 5xx error ratio
sum(rate(spt_http_requests_total{status_code=~"5.."}[5m])) / sum(rate(spt_http_requests_total[5m]))

# Memory pressure
process_resident_memory_bytes > 25 * 1024^3
```

## Notes & limitations

- The exporter listens on plain HTTP (no TLS) — put it behind your firewall or a reverse
  proxy if the machine is exposed.
- `spt_exceptions_total` only sees exceptions that flow through the SPT logger; use
  `dotnet_exceptions_total` for the complete picture.
- Counters start from zero when the server restarts, as usual for Prometheus; use `rate()` / `increase()`.
- Built for SPT `~4.1.x` on .NET 10.

## License

MIT — see [LICENSE](LICENSE). The mod is an independent work; the server it plugs into is
licensed separately by its authors.
