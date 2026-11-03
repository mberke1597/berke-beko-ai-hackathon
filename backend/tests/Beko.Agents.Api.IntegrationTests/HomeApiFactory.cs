using System.Collections.Concurrent;
using Beko.Agents.Application.Homes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beko.Agents.Api.IntegrationTests;

/// <summary>
/// Test host where <see cref="IHomeWhizClient"/> calls the mock HomeWhiz endpoints of the same in-memory server
/// and device events go to memory instead of MongoDB.
/// </summary>
public class HomeApiFactory : WebApplicationFactory<Program>
{
    public InMemoryDeviceEventStore Events { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("HomeWhiz:BaseUrl", "http://localhost/api/mock/homewhiz");
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<IHomeWhizClient, Infrastructure.HomeWhiz.HomeWhizHttpClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
            services.RemoveAll<IDeviceEventStore>();
            services.AddSingleton<IDeviceEventStore>(Events);
        });
    }
}

public sealed class InMemoryDeviceEventStore : IDeviceEventStore
{
    public ConcurrentQueue<DeviceEventRecord> Events { get; } = new();

    public Task AddAsync(DeviceEventRecord deviceEvent, CancellationToken cancellationToken)
    {
        Events.Enqueue(deviceEvent);
        return Task.CompletedTask;
    }
}
