using System.Net;
using Microsoft.Extensions.Localization;
using CreatioHelper.WebUI.Resources;

namespace CreatioHelper.WebUI.Services;

/// <summary>
/// Helper for converting technical error messages to user-friendly localized messages
/// </summary>
public interface IErrorMessageHelper
{
    /// <summary>
    /// Converts an exception to a user-friendly error message
    /// </summary>
    string GetUserFriendlyMessage(Exception ex);

    /// <summary>
    /// Checks if the exception is an authentication error (401)
    /// </summary>
    bool IsAuthenticationError(Exception ex);
}

public class ErrorMessageHelper : IErrorMessageHelper
{
    private readonly IStringLocalizer<Localization> _localizer;

    public ErrorMessageHelper(IStringLocalizer<Localization> localizer)
    {
        _localizer = localizer;
    }

    public string GetUserFriendlyMessage(Exception ex)
    {
        return ex switch
        {
            HttpRequestException httpEx => GetHttpErrorMessage(httpEx),
            TaskCanceledException => _localizer["Error_Timeout"],
            OperationCanceledException => _localizer["Error_Cancelled"],
            _ => GetGenericErrorMessage(ex)
        };
    }

    public bool IsAuthenticationError(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode == HttpStatusCode.Unauthorized)
                return true;

            var message = httpEx.Message;
            if (message.Contains("401") || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private string GetHttpErrorMessage(HttpRequestException ex)
    {
        // Try to extract status code from the exception
        if (ex.StatusCode.HasValue)
        {
            return GetHttpStatusMessage(ex.StatusCode.Value);
        }

        // Parse status code from message if available
        var message = ex.Message;

        // Handle common .NET HTTP error message patterns
        if (message.Contains("401") || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_Http401"];
        }
        if (message.Contains("403") || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_Http403"];
        }
        if (message.Contains("404") || message.Contains("NotFound", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_Http404"];
        }
        if (message.Contains("500") || message.Contains("InternalServerError", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_Http500"];
        }
        if (message.Contains("502") || message.Contains("BadGateway", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_Http502"];
        }
        if (message.Contains("503") || message.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_Http503"];
        }

        // Connection errors
        if (message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connect", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_ConnectionFailed"];
        }

        // Network unreachable
        if (message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unreachable", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_NetworkUnreachable"];
        }

        // DNS errors
        if (message.Contains("host", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("dns", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("resolve", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_HostNotFound"];
        }

        // SSL/TLS errors
        if (message.Contains("ssl", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("tls", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_SslError"];
        }

        // Fallback to generic connection error
        return _localizer["Error_ConnectionFailed"];
    }

    private string GetHttpStatusMessage(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => _localizer["Error_Http401"],
            HttpStatusCode.Forbidden => _localizer["Error_Http403"],
            HttpStatusCode.NotFound => _localizer["Error_Http404"],
            HttpStatusCode.RequestTimeout => _localizer["Error_Timeout"],
            HttpStatusCode.InternalServerError => _localizer["Error_Http500"],
            HttpStatusCode.BadGateway => _localizer["Error_Http502"],
            HttpStatusCode.ServiceUnavailable => _localizer["Error_Http503"],
            HttpStatusCode.GatewayTimeout => _localizer["Error_Timeout"],
            _ => _localizer["Error_HttpGeneric", (int)statusCode]
        };
    }

    private string GetGenericErrorMessage(Exception ex)
    {
        var message = ex.Message;

        // Check for common patterns in generic exceptions
        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_Timeout"];
        }

        if (message.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            return _localizer["Error_ConnectionFailed"];
        }

        // Return a generic error message
        return _localizer["Error_Unknown"];
    }
}
