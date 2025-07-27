using CreatioHelper.Domain.Common;

namespace CreatioHelper.Domain.ValueObjects;

public class ServerId : ValueObject
{
    public Guid Value { get; }

    public ServerId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("ServerId cannot be empty", nameof(value));
        
        Value = value;
    }

    public static ServerId Create() => new(Guid.NewGuid());
    public static ServerId From(Guid value) => new(value);

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }

    public static implicit operator Guid(ServerId serverId) => serverId.Value;
    public static implicit operator ServerId(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public class ServerName : ValueObject
{
    public string Value { get; }

    public ServerName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Server name cannot be null or empty", nameof(value));
        
        if (value.Length > 100)
            throw new ArgumentException("Server name cannot exceed 100 characters", nameof(value));

        Value = value.Trim();
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }

    public static implicit operator string(ServerName serverName) => serverName.Value;
    public static implicit operator ServerName(string value) => new(value);

    public override string ToString() => Value;
}

public class NetworkPath : ValueObject
{
    public string Value { get; }

    public NetworkPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Network path cannot be null or empty", nameof(value));

        Value = value.Trim();
    }

    public bool IsLocal => !Value.StartsWith(@"\\");

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }

    public static implicit operator string(NetworkPath networkPath) => networkPath.Value;
    public static implicit operator NetworkPath(string value) => new(value);

    public override string ToString() => Value;
}
