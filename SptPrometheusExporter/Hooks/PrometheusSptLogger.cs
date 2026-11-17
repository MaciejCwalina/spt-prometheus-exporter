using Microsoft.Extensions.Logging;
using SPTarkov.Common.Logger;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using Spectre.Console;
using SptPrometheusExporter.Metrics;

namespace SptPrometheusExporter.Hooks;

/// <summary>
/// Decorates every <see cref="ISptLogger{T}"/> in the server (registered after the built-in
/// <see cref="SptLogger{T}"/>, so it wins resolution) and counts log entries by level plus every
/// exception that flows through the logging pipeline. This is what makes "did an exception occur"
/// observable: request pipeline failures, websocket send failures (WebSocketException), database
/// errors and everything else the server logs with an Exception end up in spt_exceptions_total.
/// </summary>
[Injectable]
public sealed class PrometheusSptLogger<T>(SptLogger<T> inner, PrometheusMetrics metrics) : ISptLogger<T>
{
    public void LogWithColor(string data, Color? textColor = null, Color? backgroundColor = null, Exception? ex = null)
    {
        Count(LogLevel.Information, ex);
        inner.LogWithColor(data, textColor, backgroundColor, ex);
    }

    public void Success(string data, Exception? ex = null)
    {
        Count(LogLevel.Information, ex);
        inner.Success(data, ex);
    }

    public void Error(string data, Exception? ex = null)
    {
        Count(LogLevel.Error, ex);
        inner.Error(data, ex);
    }

    public void Warning(string data, Exception? ex = null)
    {
        Count(LogLevel.Warning, ex);
        inner.Warning(data, ex);
    }

    public void Info(string data, Exception? ex = null)
    {
        Count(LogLevel.Information, ex);
        inner.Info(data, ex);
    }

    public void Debug(string data, Exception? ex = null)
    {
        Count(LogLevel.Debug, ex);
        inner.Debug(data, ex);
    }

    public void Critical(string data, Exception? ex = null)
    {
        Count(LogLevel.Critical, ex);
        inner.Critical(data, ex);
    }

    public void Log(LogLevel level, string data, Color? textColor = null, Color? backgroundColor = null, Exception? ex = null)
    {
        Count(level, ex);
        inner.Log(level, data, textColor, backgroundColor, ex);
    }

    public bool IsLogEnabled(LogLevel level)
    {
        return inner.IsLogEnabled(level);
    }

    private void Count(LogLevel level, Exception? ex)
    {
        metrics.LogEntriesTotal.Add(LabelFormat.Build(("level", level.ToString().ToLowerInvariant())), 1);

        if (ex is not null)
        {
            metrics.ExceptionsTotal.Add(LabelFormat.Build(("type", ex.GetType().Name)), 1);
        }
    }
}
