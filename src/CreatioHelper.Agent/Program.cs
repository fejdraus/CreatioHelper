using CreatioHelper.Agent.Services;
using CreatioHelper.Application.Extensions;
using CreatioHelper.Infrastructure.Extensions;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Performance;

var builder = WebApplication.CreateBuilder(args);

// Добавляем Application Insights
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Health Checks
builder.Services.AddHealthChecks();

builder.Services.AddApplication();
builder.Services.AddInfrastructureServices();

// Добавляем новые сервисы производительности
builder.Services.AddScoped<BatchOperationService>();
builder.Services.AddScoped<ApplicationInsightsMetricsService>();

// Добавляем расширенные сервисы поддержки для достижения 10/10
builder.Services.AddScoped<EnhancedLoggingService>();
builder.Services.AddScoped<AlertingService>();
builder.Services.AddScoped<DiagnosticsService>();
builder.Services.AddScoped<CreatioSystemHealthCheck>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

builder.Services.Configure<AgentConfig>(builder.Configuration.GetSection("AgentConfig"));
builder.Services.AddPlatformServices();
builder.Services.AddPerformanceServices(); // Добавляем систему метрик

var app = builder.Build();

// Graceful shutdown configuration
var applicationLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
applicationLifetime.ApplicationStopping.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("🛑 Application shutdown initiated - stopping operations gracefully");
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Добавляем Health Checks endpoint
app.MapHealthChecks("/health");

app.UseCors("AllowAll");
app.UseRouting();
app.MapControllers();

app.MapHub<CreatioHelper.Agent.Hubs.MonitoringHub>("/monitoringHub");

// Prometheus метрики endpoint
app.MapGet("/metrics", async (CreatioHelper.Application.Interfaces.IMetricsService metricsService) =>
{
    var metrics = await metricsService.GetMetricsAsync();
    var prometheusFormat = ConvertToPrometheusFormat(metrics);
    return Results.Text(prometheusFormat, "text/plain");
});

app.MapGet("/test-signalr", () => Results.Content("""
                                                  <!DOCTYPE html>
                                                  <html>
                                                  <head>
                                                      <script src="https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/6.0.1/signalr.min.js"></script>
                                                  </head>
                                                  <body>
                                                      <h1>SignalR Test</h1>
                                                      <div id="messages"></div>
                                                      
                                                      <script>
                                                          console.log("🚀 Starting SignalR connection...");
                                                          
                                                          const connection = new signalR.HubConnectionBuilder()
                                                              .withUrl("/monitoringHub")
                                                              .configureLogging(signalR.LogLevel.Debug)
                                                              .build();
                                                  
                                                          connection.start().then(() => {
                                                              console.log("Connected!");
                                                              document.getElementById("messages").innerHTML += "<p>Connected!</p>";
                                                              return connection.invoke("JoinGroup", "monitoring");
                                                          }).then(() => {
                                                              console.log("Joined monitoring group");
                                                              document.getElementById("messages").innerHTML += "<p>Joined monitoring group</p>";
                                                          }).catch(err => {
                                                              console.error("Connection error:", err);
                                                              document.getElementById("messages").innerHTML += `<p>Error: ${err}</p>`;
                                                          });
                                                      </script>
                                                  </body>
                                                  </html>
                                                  """, "text/html"));

app.Run();

// Вспомогательная функция для конвертации в Prometheus формат
string ConvertToPrometheusFormat(Dictionary<string, object> metrics)
{
    var lines = new List<string>();
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    if (metrics.TryGetValue("counters", out var countersObj) && countersObj is Dictionary<string, object> counters)
    {
        foreach (var counter in counters)
        {
            var name = SanitizeMetricName(counter.Key);
            lines.Add($"# TYPE {name} counter");
            lines.Add($"{name} {counter.Value} {timestamp}");
        }
    }

    if (metrics.TryGetValue("gauges", out var gaugesObj) && gaugesObj is Dictionary<string, object> gauges)
    {
        foreach (var gauge in gauges)
        {
            var name = SanitizeMetricName(gauge.Key);
            lines.Add($"# TYPE {name} gauge");
            lines.Add($"{name} {gauge.Value} {timestamp}");
        }
    }

    return string.Join("\n", lines);
}

string SanitizeMetricName(string name)
{
    return name.Replace("[", "_").Replace("]", "").Replace(",", "_").Replace("=", "_").Replace(" ", "_").ToLower();
}
