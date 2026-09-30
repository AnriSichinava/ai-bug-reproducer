namespace BugReproducer.Api.Services;

/// A minimal in-process work queue for running a reproduction after the HTTP request
/// that triggered it has already returned 202. This is the standard ASP.NET Core
/// pattern for "queue a background work item" -- deliberately not a message broker or
/// external job system, which would be real overkill for a single-instance portfolio
/// project.
public interface IBackgroundTaskQueue
{
    ValueTask QueueAsync(Func<IServiceProvider, CancellationToken, Task> workItem);

    ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
}
