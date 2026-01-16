using CreatioHelper.Agent.Services;
using CreatioHelper.Agent.Configuration;
using CreatioHelper.Agent.Hubs;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Performance;
using CreatioHelper.Infrastructure.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using System.Text;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
builder.Host.UseSerilog((context, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/agent-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            fileSizeLimitBytes: 10_485_760, // 10 MB
            rollOnFileSizeLimit: true);
});

builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddControllers();
builder.Services.AddSignalR();

#if DEBUG
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "CreatioHelper Agent API",
        Version = "v1",
        Description = @"CreatioHelper Agent monitoring and management API

**How to authenticate:**
1. Use POST /api/auth/token with username=admin, password=admin123
2. Copy the returned token
3. Click 'Authorize' button below and enter: Bearer {your-token}
4. Now you can call protected endpoints"
    });

    // JWT Authorization
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Description = @"JWT Authorization header using the Bearer scheme.
                      Enter 'Bearer' [space] and then your token in the text input below.
                      Example: 'Bearer 12345abcdef'",
        Name = "Authorization",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
});
#endif

builder.Services.AddHealthChecks();

// Configuration binding
builder.Services.Configure<AuthenticationSettings>(builder.Configuration.GetSection("Authentication"));
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("JwtSettings"));
builder.Services.Configure<SwaggerAuthSettings>(builder.Configuration.GetSection("SwaggerAuth"));

// JWT Authentication
var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>() ?? new JwtSettings();

// Validate JWT Secret is configured
if (string.IsNullOrWhiteSpace(jwtSettings.Secret) || jwtSettings.Secret.Length < 32)
{
    if (builder.Environment.IsDevelopment())
    {
        // Generate random secret for development - prevents accidental production exposure
        var randomBytes = new byte[32];
        RandomNumberGenerator.Fill(randomBytes);
        jwtSettings.Secret = Convert.ToBase64String(randomBytes);
        Log.Warning("Generated random JWT secret for development. Tokens will not persist across restarts. Configure JwtSettings:Secret for stable development.");
    }
    else
    {
        throw new InvalidOperationException(
            "JWT Secret is not configured or is too short (minimum 32 characters). " +
            "Configure JwtSettings:Secret via environment variable 'JwtSettings__Secret' or in appsettings.json");
    }
}

// Set defaults if not configured
jwtSettings.Issuer ??= "CreatioHelper.Agent";
jwtSettings.Audience ??= "CreatioHelper.Client";
if (jwtSettings.ExpirationHours <= 0) jwtSettings.ExpirationHours = 24;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Secret))
        };
        
        // For SignalR
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/monitoringHub"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Rate limiting for login endpoint
builder.Services.AddSingleton<CreatioHelper.Agent.Services.LoginRateLimiter>();

builder.Services.AddApplication();
builder.Services.AddInfrastructureServices(builder.Configuration);

// Register performance services
builder.Services.AddScoped<ApplicationInsightsMetricsService>();

// Register additional support services
builder.Services.AddScoped<EnhancedLoggingService>();
builder.Services.AddScoped<AlertingService>();
builder.Services.AddScoped<DiagnosticsService>();
builder.Services.AddScoped<CreatioSystemHealthCheck>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

        if (allowedOrigins?.Length > 0)
        {
            // Use configured origins in production
            policy.WithOrigins(allowedOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        }
        else if (builder.Environment.IsDevelopment())
        {
            // Allow any origin only in development (without credentials for security)
            policy.SetIsOriginAllowed(_ => true)
                .AllowAnyMethod()
                .AllowAnyHeader();
        }
        else
        {
            // Default: localhost only in production if no origins configured
            policy.WithOrigins("http://localhost", "https://localhost")
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        }
    });
});

builder.Services.Configure<AgentConfig>(builder.Configuration.GetSection("AgentConfig"));
builder.Services.AddPlatformServices();
builder.Services.AddPerformanceServices();
builder.Services.AddSyncthingAutoStop(builder.Configuration);

// Add heartbeat service
builder.Services.AddHostedService<HeartbeatService>();

// Add sync services with configuration
var syncConfigFromFile = builder.Configuration.GetSection("Sync").Get<SyncConfigurationFromFile>();
SyncConfiguration? syncConfig = null;

if (syncConfigFromFile != null)
{
    Log.Debug("Loaded sync config: DeviceId={DeviceId}, Port={Port}", syncConfigFromFile.DeviceId, syncConfigFromFile.Port);

    // Convert from file config to domain config
    syncConfig = new SyncConfiguration(syncConfigFromFile.DeviceId, syncConfigFromFile.DeviceName);
    syncConfig.SetPort(syncConfigFromFile.Port);
    syncConfig.SetDiscoveryPort(syncConfigFromFile.DiscoveryPort);
}
else
{
    Log.Debug("No sync config found in appsettings");
}
builder.Services.AddSyncServices(syncConfig);

var app = builder.Build();

// Graceful shutdown configuration
var applicationLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
applicationLifetime.ApplicationStopping.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("🛑 Application shutdown initiated - stopping operations gracefully");
});

#if DEBUG
// Swagger configuration - only enabled in development for security
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "CreatioHelper Agent API v1");
        c.DocumentTitle = "CreatioHelper Agent API";
        c.DefaultModelsExpandDepth(-1);
    });
}
#endif

// Security Headers middleware
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    // Add HSTS header for HTTPS requests (1 year)
    if (context.Request.IsHttps)
    {
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }

    await next();
});

// Expose health checks endpoint
app.MapHealthChecks("/health");

app.UseCors("AllowAll");
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapHub<CreatioHelper.Agent.Hubs.MonitoringHub>("/monitoringHub");
app.MapHub<SyncHub>("/syncHub");

// Prometheus metrics endpoint (requires authentication)
app.MapGet("/metrics", async (IMetricsService metricsService) =>
{
    var metrics = await metricsService.GetMetricsAsync();
    var prometheusFormat = ConvertToPrometheusFormat(metrics);
    return Results.Text(prometheusFormat, "text/plain");
}).RequireAuthorization();

// SignalR test page - only enabled in development
if (app.Environment.IsDevelopment())
{
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
}

try
{
    Log.Information("Starting CreatioHelper Agent");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Helper function to convert metrics to Prometheus format
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
