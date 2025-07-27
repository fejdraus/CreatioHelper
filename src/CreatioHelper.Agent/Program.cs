using CreatioHelper.Agent.Services;
using CreatioHelper.Application.Extensions;
using CreatioHelper.Infrastructure.Extensions;
using CreatioHelper.Domain.Entities;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddApplication();
builder.Services.AddInfrastructureServices();
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
builder.Services.AddHostedService<MonitoringService>();
var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");
app.UseRouting();
app.MapControllers();
//app.UseAuthorization();
app.MapHub<CreatioHelper.Agent.Hubs.MonitoringHub>("/monitoringHub");
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