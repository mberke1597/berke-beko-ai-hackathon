using System.Diagnostics;
using Beko.Agents.Api.Celiknaz;
using Beko.Agents.Api.Hubs;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Application.Homes;
using Beko.Agents.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddSignalR();
builder.Services.AddSingleton<IDeviceNotifier, SignalRDeviceNotifier>();
builder.Services.AddSingleton<IRunNotifier, SignalRRunNotifier>();
builder.Services.AddCeliknazHome(builder.Configuration);

var app = builder.Build();

// Development only: apply EF Core migrations on startup. Elsewhere run `dotnet ef database update` (see backend/README.md).
if (app.Environment.IsDevelopment() && app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await app.Services.MigrateDatabaseAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(DependencyInjection.ReadyTag),
});
app.MapControllers();
app.MapHub<DeviceHub>(DeviceHub.Path);
app.MapHub<RunHub>(RunHub.Path);

await app.RunAsync();
