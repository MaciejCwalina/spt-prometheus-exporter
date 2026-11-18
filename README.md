# SPT Prometheus Exporter

A server mod for [Single Player Tushonka](https://github.com/SP-Tushonka/server-csharp) that exposes
server observability metrics in the [Prometheus exposition format](https://prometheus.io/docs/instrumenting/exposition_formats/).

No server code is modified — the mod hooks into existing extension points:

| Hook | What it observes |
|---|---|
| `IWebSocketConnectionHandler` (second registration on the notifier endpoint) | Player websocket connect/disconnect + every packet received from players |
| `ISptLogger<T>` decorator (registered after the built-in logger) | Every log entry by level, and every exception carried through the logging pipeline |
| `IOnLoad` hook | Process-level unhandled exceptions (`AppDomain`, unobserved tasks) |
| `MeterListener` on ASP.NET Core / Kestrel / .NET runtime meters | HTTP request counts + latency, Kestrel connection stats, .NET exception counter, GC, CPU, memory |
| `BackgroundService` | Serves `/metrics` on its own port, samples uptime/threads |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A checkout of the [server repository](https://github.com/SP-Tushonka/server-csharp) at `../server-csharp`
  (i.e. a sibling of this repo — `build.sh` clones it for you if missing)

## Building

```bash
./build.sh            # clones ../server-csharp if needed, builds, copies the DLL
```

The compiled mod ends up in the server's
`SPTushonka.Server/bin/Debug/net10.0/user/mods/SptPrometheusExporter/` folder, which is where the
server picks mods up from. Start the server normally; you should see:

```
Mod: spt-prometheus-exporter version: 1.0.0 (...) loaded
Prometheus exporter listening on http://0.0.0.0:9464/metrics
```

### Manual layout (no build script)

If you prefer to keep everything in one tree, copy this repo's `SptPrometheusExporter/` folder into
`<server-csharp>/Mods/` and build the server solution — the in-repo variant does exactly that.

## Configuration

Editable in the SPT config editor (appears as "Prometheus Exporter"), or directly in
`user/mods/SptPrometheusExporter/config.json`:

```json
{
  "enabled": true,
  "port": 9464,
  "bindAddress": "0.0.0.0",
  "sampleIntervalSeconds": 15
}
```

The port can also be overridden with the `SPT_PROMETHEUS_PORT` environment variable.

## Metrics

### Server lifecycle
| Metric | Type | Description |
|---|---|---|
| `spt_up` | gauge | Process is up (1) |
| `spt_uptime_seconds` | gauge | Uptime |
| `spt_version_info{version}` | gauge | SPT version (value always 1) |

### Players / websocket ("packet" layer)
| Metric | Type | Description |
|---|---|---|
| `spt_players_connected` | gauge | Currently open player notifier connections |
| `spt_ws_connections_total{session}` | counter | Connections opened per session id |
| `spt_ws_disconnections_total{session}` | counter | Connections closed per session id |
| `spt_ws_packets_received_total{session}` | counter | **Every message received from a player** (per session) |
| `spt_ws_upgraded_connections_total` | counter | Kestrel WS upgrades |
| `spt_session_info{session,username}` | gauge | Session id → player username mapping (value always 1); use with `group_left(username)` joins to show names in dashboards |

**Packet loss semantics:** the EFT notifier protocol has no client acks, so true end-to-end
loss is not observable server-side. What this mod measures:
- received packet rate per player (`spt_ws_packets_received_total`) — a stalled/dead client
  shows up as a flatlined rate;
- send-side delivery failures surface as `WebSocketException` in `spt_exceptions_total` /
  `dotnet_exceptions_total`;
- connections that were never established show up in `spt_ws_connections_total` vs
  `spt_players_connected`.

### Exceptions
| Metric | Type | Description |
|---|---|---|
| `spt_exceptions_total{type}` | counter | Exceptions logged anywhere in the server (via the `ISptLogger<T>` decorator), by .NET exception type |
| `dotnet_exceptions_total{type}` | counter | All exceptions thrown in the process, straight from the .NET runtime meter |
| `spt_unhandled_exceptions_total{source,type}` | counter | Exceptions that bypassed logging entirely (`appdomain`, `task_scheduler`) |
| `spt_log_entries_total{level}` | counter | Log volume by level (debug/info/warning/error/critical) |

### HTTP
| Metric | Type | Description |
|---|---|---|
| `spt_http_requests_total{method,protocol,status_code,route}` | counter | Request counts (route-based, bounded cardinality) |
| `spt_http_request_duration_seconds{method,protocol,status_code,route}` | histogram | Request latency (`_bucket{le}`, `_sum`, `_count`; buckets 5ms to 10s) |
| `kestrel_active_connections` | gauge | Active Kestrel connections |
| `kestrel_queued_requests` | gauge | Requests waiting in the Kestrel queue |

### Process / runtime
| Metric | Type | Description |
|---|---|---|
| `process_memory_bytes` | gauge | Working set |
| `process_threads` | gauge | Thread count |
| `process_cpu_seconds_total` | counter | CPU time |
| `dotnet_thread_pool_threads` | gauge | ThreadPool threads |
| `dotnet_gc_collections_total{generation}` | counter | GC collections by generation |
| `dotnet_gc_pause_time_seconds_total` | counter | GC pause time |

## Prometheus scrape config

```yaml
scrape_configs:
  - job_name: spt
    static_configs:
      - targets: ["<server-ip>:9464"]
```

## Example alerting queries

```promql
# Server down
absent(spt_up)

# Exception spike (any type)
rate(dotnet_exceptions_total[5m]) > 1

# A specific player stopped sending packets (stalled client)
deriv(spt_ws_packets_received_total{session="..."}[5m]) == 0

# Slow requests
rate(spt_http_request_duration_seconds_sum[5m]) / rate(spt_http_request_duration_seconds_count[5m]) > 1

# p95 request latency per route
histogram_quantile(0.95, sum by (le, route) (rate(spt_http_request_duration_seconds_bucket[5m])))

# Memory pressure
process_memory_bytes > 25 * 1024^3
```

## Notes & limitations

- The exporter listens on plain HTTP (no TLS) — put it behind your firewall or a reverse
  proxy if the machine is exposed.
- `spt_exceptions_total` only sees exceptions that flow through the SPT logger; use
  `dotnet_exceptions_total` for the complete picture.
- Built for SPT `~4.1.x` on .NET 10.

## License

MIT — see [LICENSE](LICENSE). The mod is an independent work; the server it plugs into is
licensed separately by its authors.
