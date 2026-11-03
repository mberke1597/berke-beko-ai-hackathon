using System.Diagnostics;
using Beko.Agents.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(DependencyInjection.ReadyTag),
});
app.MapControllers();

await app.RunAsync();
