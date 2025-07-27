namespace CreatioHelper.Domain.Common;

public abstract class DomainEvent
{
    public DateTime OccurredOn { get; protected set; } = DateTime.UtcNow;
    public Guid Id { get; protected set; } = Guid.NewGuid();
}

public interface IDomainEventHandler<in T> where T : DomainEvent
{
    Task Handle(T domainEvent, CancellationToken cancellationToken = default);
}
