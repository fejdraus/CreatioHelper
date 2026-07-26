using Serilog.Core;
using Serilog.Events;

namespace CreatioHelper.Agent.Logging;

public class SensitiveQueryStringEnricher : ILogEventEnricher
{
    private const string QueryStringProperty = "QueryString";
    private const string Redacted = "REDACTED";

    private static readonly string[] SensitiveParameters =
    {
        "access_token",
        "token",
        "id_token",
        "refresh_token",
        "apikey",
        "api_key",
        "password",
        "secret"
    };

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (!logEvent.Properties.TryGetValue(QueryStringProperty, out var property))
        {
            return;
        }

        if (property is not ScalarValue { Value: string queryString } || queryString.Length == 0)
        {
            return;
        }

        var redacted = Redact(queryString);
        if (ReferenceEquals(redacted, queryString))
        {
            return;
        }

        logEvent.AddOrUpdateProperty(new LogEventProperty(QueryStringProperty, new ScalarValue(redacted)));
    }

    public static string Redact(string queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return queryString;
        }

        var prefix = queryString.StartsWith('?') ? "?" : string.Empty;
        var body = prefix.Length == 0 ? queryString : queryString[1..];

        if (body.Length == 0)
        {
            return queryString;
        }

        var pairs = body.Split('&');
        var changed = false;

        for (var i = 0; i < pairs.Length; i++)
        {
            var separatorIndex = pairs[i].IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = pairs[i][..separatorIndex];
            if (!IsSensitive(name))
            {
                continue;
            }

            pairs[i] = string.Concat(name, "=", Redacted);
            changed = true;
        }

        return changed ? prefix + string.Join('&', pairs) : queryString;
    }

    private static bool IsSensitive(string name)
    {
        foreach (var sensitive in SensitiveParameters)
        {
            if (string.Equals(name, sensitive, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
