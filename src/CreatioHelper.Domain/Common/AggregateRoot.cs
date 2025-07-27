using CreatioHelper.Domain.Common;

namespace CreatioHelper.Domain.Common;

public abstract class AggregateRoot : Entity
{
    protected AggregateRoot(Guid id) : base(id) { }
    protected AggregateRoot() : base() { }

    /// <summary>
    /// Применяет доменное событие к агрегату
    /// </summary>
    protected void Apply(DomainEvent domainEvent)
    {
        AddDomainEvent(domainEvent);
    }

    /// <summary>
    /// Проверяет инварианты агрегата
    /// </summary>
    public abstract bool IsValid();

    /// <summary>
    /// Получает все нарушения бизнес-правил
    /// </summary>
    public abstract IEnumerable<string> GetBrokenRules();
}
