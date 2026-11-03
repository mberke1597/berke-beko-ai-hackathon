using System.Threading.Channels;
using Beko.Agents.Application.Agents;
using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Application.Agents.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Beko.Agents.Infrastructure.Agents;

/// <summary>Bounded in-process run queue. A full queue rejects new runs instead of blocking the request.</summary>
public sealed class ChannelRunDispatcher : IRunDispatcher
{
    public const int Capacity = 100;

    private readonly Channel<RunRequest> _channel = Channel.CreateBounded<RunRequest>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false,
    });

    public ChannelReader<RunRequest> Reader => _channel.Reader;

    public bool TryEnqueue(RunRequest request) => _channel.Writer.TryWrite(request);
}

/// <summary>Executes queued runs in the background, a few at a time, each in its own DI scope.</summary>
public sealed partial class RunWorker(ChannelRunDispatcher dispatcher, IServiceScopeFactory scopes, ILogger<RunWorker> logger) : BackgroundService
{
    /// <summary>Runs executed in parallel (the VM has 2 OCPUs; runs mostly wait on the LLM).</summary>
    public const int Concurrency = 4;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => ProcessAsync(stoppingToken)));

    private async Task ProcessAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in dispatcher.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
                    await engine.ExecuteAsync(request, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRunCrashed(logger, ex, request.RunId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId} crashed outside the workflow engine")]
    private static partial void LogRunCrashed(ILogger logger, Exception exception, string runId);
}
