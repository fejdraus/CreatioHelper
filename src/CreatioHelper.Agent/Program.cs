using CreatioHelper.Agent.Services;
using CreatioHelper.Agent.Configuration;
using CreatioHelper.Agent.Hubs;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Performance;
using CreatioHelper.Infrastructure.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
#if DEBUG
using Microsoft.OpenApi.Models;
#endif

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddControllers();
builder.Services.AddSignalR();

#if DEBUG
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo 
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
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = @"JWT Authorization header using the Bearer scheme.
                      Enter 'Bearer' [space] and then your token in the text input below.
                      Example: 'Bearer 12345abcdef'",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
#endif

builder.Services.AddHealthChecks();

// Configuration binding
builder.Services.Configure<AuthenticationSettings>(builder.Configuration.GetSection("Authentication"));
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("JwtSettings"));
builder.Services.Configure<SwaggerAuthSettings>(builder.Configuration.GetSection("SwaggerAuth"));

// JWT Authentication
var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>() ?? new JwtSettings
{
    Secret = "YourSuperSecretKeyThatIsAtLeast32CharactersLong!",
    Issuer = "CreatioHelper.Agent",
    Audience = "CreatioHelper.Client",
    ExpirationHours = 24
};

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
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

builder.Services.Configure<AgentConfig>(builder.Configuration.GetSection("AgentConfig"));
builder.Services.AddPlatformServices();
builder.Services.AddPerformanceServices();

// Add sync services with configuration
var syncConfigFromFile = builder.Configuration.GetSection("Sync").Get<SyncConfigurationFromFile>();
SyncConfiguration? syncConfig = null;

if (syncConfigFromFile != null)
{
    Console.WriteLine($"Loaded sync config: DeviceId={syncConfigFromFile.DeviceId}, Port={syncConfigFromFile.Port}");
    
    // Convert from file config to domain config
    syncConfig = new SyncConfiguration(syncConfigFromFile.DeviceId, syncConfigFromFile.DeviceName);
    syncConfig.SetPort(syncConfigFromFile.Port);
    syncConfig.SetDiscoveryPort(syncConfigFromFile.DiscoveryPort);
}
else
{
    Console.WriteLine("No sync config found in appsettings");
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
// Swagger configuration with security
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
else
{
    // In production, require authentication for Swagger
    var swaggerAuthSettings = builder.Configuration.GetSection("SwaggerAuth").Get<SwaggerAuthSettings>();
    
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "CreatioHelper Agent API v1");
        c.DocumentTitle = "CreatioHelper Agent API";
        c.DefaultModelsExpandDepth(-1);
    });
    
    // Simple basic auth middleware for Swagger in production
    if (swaggerAuthSettings?.Enabled == true)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/swagger"))
            {
                string? authHeader = context.Request.Headers["Authorization"];
                if (authHeader != null && authHeader.StartsWith("Basic "))
                {
                    var encodedUsernamePassword = authHeader["Basic ".Length..].Trim();
                    var decodedUsernamePassword = Encoding.UTF8.GetString(Convert.FromBase64String(encodedUsernamePassword));
                    var username = decodedUsernamePassword.Split(':', 2)[0];
                    var password = decodedUsernamePassword.Split(':', 2)[1];
                    
                    if (username == swaggerAuthSettings.Username && password == swaggerAuthSettings.Password)
                    {
                        await next();
                        return;
                    }
                }
                
                context.Response.Headers["WWW-Authenticate"] = "Basic";
                context.Response.StatusCode = 401;
                return;
            }
            await next();
        });
    }
}
#endif

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
