using Microsoft.AspNetCore.Diagnostics;

namespace CreatioHelper.Agent.Middleware;

public sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ApiExceptionHandler> _logger;

    public ApiExceptionHandler(ILogger<ApiExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var endpoint = httpContext.GetEndpoint()?.DisplayName ?? httpContext.Request.Path.Value;
        var routeValues = string.Join(
            ", ",
            httpContext.Request.RouteValues
                .Where(v => v.Value is not null)
                .Select(v => $"{v.Key}={v.Value}"));

        _logger.LogError(
            exception,
            "Unhandled exception in {Method} {Endpoint} [{RouteValues}]",
            httpContext.Request.Method,
            endpoint,
            routeValues);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response
            .WriteAsJsonAsync(new { error = "Internal server error" }, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}
