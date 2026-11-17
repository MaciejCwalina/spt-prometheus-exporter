using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SptPrometheusExporter.Metrics;

namespace SptPrometheusExporter.Hooks;

/// <summary>
/// Catches process-level exceptions that bypass the logging pipeline entirely:
/// unhandled exceptions on any thread and unobserved task exceptions.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public sealed class ExceptionWatcher(PrometheusMetrics metrics) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exceptionType = args.ExceptionObject?.GetType().Name ?? "unknown";
            metrics.UnhandledExceptionsTotal.Add(
                LabelFormat.Build(("source", "appdomain"), ("type", exceptionType)),
                1
            );
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            metrics.UnhandledExceptionsTotal.Add(
                LabelFormat.Build(("source", "task_scheduler"), ("type", args.Exception.GetType().Name)),
                1
            );
        };

        return Task.CompletedTask;
    }
}
