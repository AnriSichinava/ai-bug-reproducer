namespace BugReproducer.Api.Services;

/// Drains the queue for the lifetime of the app. Each work item gets its own DI scope,
/// so it gets its own AppDbContext (DbContext isn't thread-safe / shareable across
/// concurrent work) and, in production, its own browser via a scoped IBrowserTools.
public class QueuedHostedService(IBackgroundTaskQueue queue, IServiceProvider services, ILogger<QueuedHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var workItem = await queue.DequeueAsync(stoppingToken);

            try
            {
                using var scope = services.CreateScope();
                await workItem(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed work item must not take the host down -- the next queued run
                // (or the next person's bug report) still deserves to be processed.
                logger.LogError(ex, "Background work item failed.");
            }
        }
    }
}
