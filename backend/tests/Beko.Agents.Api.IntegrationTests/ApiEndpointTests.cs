using System.Net;
using System.Text.Json;
using Beko.Agents.Application.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Api.IntegrationTests;

public sealed class ApiEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_ReturnsHealthy()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Version_ReturnsExpectedJsonShape()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/version", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = json.RootElement;
        Assert.Equal("Beko Agent Platform", root.GetProperty("name").GetString());
        Assert.Equal("0.1.0", root.GetProperty("version").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("commit").GetString()));
        Assert.Equal("Development", root.GetProperty("environment").GetString());
        Assert.Equal(DateTimeKind.Utc, root.GetProperty("serverTimeUtc").GetDateTime().Kind);
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemDetailsWithTraceId()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public void Pricing_FromAppSettings_ContainsDefaultModels()
    {
        var pricing = factory.Services.GetRequiredService<IOptions<PricingOptions>>().Value;
        var anthropic = factory.Services.GetRequiredService<IOptions<AnthropicOptions>>().Value;

        Assert.Contains(anthropic.DefaultModel, pricing.Models.Keys);
        Assert.Contains(anthropic.FastModel, pricing.Models.Keys);
    }

    [Fact]
    public void Production_WithoutAnthropicApiKey_FailsOnStart()
    {
        using var productionFactory = CreateProductionFactory(apiKey: "", signingKey: "0123456789abcdef0123456789abcdef");

        var exception = Record.Exception(() => productionFactory.CreateClient());

        Assert.Contains("Anthropic:ApiKey is required.", GetOptionsValidationFailures(exception));
    }

    [Fact]
    public void Production_WithoutJwtSigningKey_FailsOnStart()
    {
        using var productionFactory = CreateProductionFactory(apiKey: "test-key", signingKey: "");

        var exception = Record.Exception(() => productionFactory.CreateClient());

        Assert.Contains("Jwt:SigningKey is required.", GetOptionsValidationFailures(exception));
    }

    /// <summary>
    /// <c>ValidateOnStart</c> throws a single <see cref="OptionsValidationException"/> when one options type fails
    /// and an <see cref="AggregateException"/> of them when several fail; collect the messages in both cases.
    /// </summary>
    private static List<string> GetOptionsValidationFailures(Exception? exception)
    {
        Assert.NotNull(exception);

        var exceptions = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception];
        var failures = exceptions.OfType<OptionsValidationException>().SelectMany(e => e.Failures).ToList();

        Assert.NotEmpty(failures);
        return failures;
    }

    private WebApplicationFactory<Program> CreateProductionFactory(string apiKey, string signingKey) =>
        factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=beko;Username=beko")
            .UseSetting("Mongo:ConnectionString", "mongodb://localhost:27017")
            .UseSetting("Anthropic:ApiKey", apiKey)
            .UseSetting("Jwt:SigningKey", signingKey));
}
