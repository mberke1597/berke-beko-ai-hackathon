using Beko.Agents.Application.Agents.Runs;
using Beko.Agents.Application.Options;
using Beko.Agents.Domain.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Agents.Workflow;

/// <summary>Executes one workflow step type. Adding a step type (guard.input, knowledge.search, ...) means adding an executor.</summary>
public interface IStepExecutor
{
    string Type { get; }

    Task ExecuteAsync(RunContext context, WorkflowStep step, StepScope scope, RunScope run);
}

/// <summary>
/// Runs an agent's active workflow for one user message: loads the definition, then executes the top-level steps
/// in order, each through the step wrapper. A failing step fails the run and produces the Turkish fallback answer.
/// </summary>
public sealed partial class WorkflowEngine(
    IAgentRepository agents,
    IEnumerable<IStepExecutor> executors,
    RunRecorder recorder,
    IConversationStore conversations,
    IOptions<AnthropicOptions> anthropic,
    TimeProvider timeProvider,
    ILogger<WorkflowEngine> logger)
{
    public const string FallbackAnswer = "Şu anda yanıt veremiyorum, lütfen tekrar deneyin.";

    private readonly Dictionary<string, IStepExecutor> _executors = executors.ToDictionary(e => e.Type, StringComparer.Ordinal);

    public async Task<RunRecord> ExecuteAsync(RunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Agent? agent = null;
        WorkflowDefinition? workflow = null;
        Exception? loadError = null;
        try
        {
            agent = await agents.GetAgentAsync(request.AgentId, cancellationToken);
            workflow = agent is null ? null : await agents.GetActiveWorkflowAsync(agent.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Still produce a failed run with the fallback answer: every run ends with exactly one RunCompleted.
            loadError = ex;
        }

        var model = string.IsNullOrWhiteSpace(agent?.Model) ? anthropic.Value.DefaultModel : agent.Model;
        var context = new RunContext(request, agent, model, workflow?.Version ?? 0);

        var run = await recorder.RunAsync(
            context,
            async scope =>
            {
                if (loadError is not null)
                {
                    throw new InvalidOperationException("Could not load the agent configuration.", loadError);
                }

                if (agent is null || workflow is null)
                {
                    throw new InvalidOperationException($"Agent '{request.AgentId}' or its active workflow does not exist.");
                }

                foreach (var step in workflow.Steps.Where(s => s.ParentStepId is null).OrderBy(s => s.Position))
                {
                    await scope.RunStepAsync(
                        step.Type,
                        step.Name,
                        null,
                        stepScope => ResolveExecutor(step.Type).ExecuteAsync(context, step, stepScope, scope),
                        cancellationToken);
                }
            },
            cancellationToken);

        if (run.Status == RunStatuses.Failed)
        {
            context.AnswerText = FallbackAnswer;
            try
            {
                await conversations.AddMessageAsync(
                    new MessageRecord(
                        Guid.NewGuid().ToString("N"),
                        request.ConversationId,
                        request.AgentId,
                        request.UserId,
                        MessageRoles.Assistant,
                        FallbackAnswer,
                        request.Channel,
                        request.RunId,
                        timeProvider.GetUtcNow().UtcDateTime,
                        [.. context.Actions],
                        run.TraceId,
                        run.SpanId),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFallbackNotStored(logger, ex, request.RunId);
            }
        }

        await recorder.CompleteAsync(context, run, CancellationToken.None);
        return run;
    }

    private IStepExecutor ResolveExecutor(string type) =>
        _executors.TryGetValue(type, out var executor)
            ? executor
            : throw new InvalidOperationException($"No executor is registered for step type '{type}'.");

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not store the fallback answer of run {RunId}")]
    private static partial void LogFallbackNotStored(ILogger logger, Exception exception, string runId);
}
