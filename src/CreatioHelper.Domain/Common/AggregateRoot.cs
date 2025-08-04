namespace CreatioHelper.Domain.Common;

public abstract class AggregateRoot : Entity
{
    protected AggregateRoot(Guid id) : base(id) { }
    protected AggregateRoot() : base() { }

    /// <summary>
    /// Applies a domain event to the aggregate.
    /// </summary>
    protected void Apply(DomainEvent domainEvent)
    {
        AddDomainEvent(domainEvent);
    }

    /// <summary>
    /// Validates aggregate invariants.
    /// </summary>
    public abstract bool IsValid();

    /// <summary>
    /// Returns all broken business rules.
    /// </summary>
    public abstract IEnumerable<string> GetBrokenRules();
}
