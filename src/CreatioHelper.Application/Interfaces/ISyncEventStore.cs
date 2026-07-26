using CreatioHelper.Domain.Entities.Events;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Durable storage for sync events. Without it the event history lives only in
/// the in-memory ring buffer and is lost on restart.
/// </summary>
public interface ISyncEventStore
{
    Task AppendAsync(IReadOnlyList<SyncEvent> events, CancellationToken cancellationToken = default);

    Task<List<SyncEvent>> LoadRecentAsync(int limit, CancellationToken cancellationToken = default);
}
