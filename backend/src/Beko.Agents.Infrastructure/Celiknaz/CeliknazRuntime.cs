using Beko.Agents.Application;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Comfort;
using Beko.Agents.Application.Energy;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Learning;
using Beko.Agents.Application.Options;
using Beko.Agents.Application.Presence;
using Beko.Agents.Application.Tasks;
using Beko.Agents.Infrastructure.HomeWhiz.Mock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Infrastructure.Celiknaz;

/// <summary>The simulator's clock seen as the home clock, with the home's UTC offset from <see cref="EnergyOptions"/>.</summary>
internal sealed class SimulationHomeClock(SimulationClock clock, IOptions<SimulationOptions> simulation, IOptions<EnergyOptions> energy) : IHomeClock
{
    public DateTime UtcNow => clock.UtcNow;

    public double TimeScale => simulation.Value.TimeScale;

    public TimeSpan UtcOffset => TimeSpan.FromHours(energy.Value.HomeUtcOffsetHours);
}

/// <summary>Sends automatic commands through <see cref="DeviceService"/> (scoped) so they are recorded and published like any command.</summary>
internal sealed class AiCommandSender(IServiceScopeFactory scopes) : IAiCommandSender
{
    public async Task<AiCommandOutcome> SendAsync(
        string applianceId,
        Domain.Homes.ApplianceCommand command,
        string memberId,
        CancellationToken cancellationToken,
        string source = "ai")
    {
        await using var scope = scopes.CreateAsyncScope();
        var devices = scope.ServiceProvider.GetRequiredService<DeviceService>();
        try
        {
            var result = await devices.SendCommandAsync(applianceId, new DeviceCommand(command, source, memberId), cancellationToken);
            return new AiCommandOutcome(result?.Accepted == true, null);
        }
        catch (ApplianceCommandRejectedException ex)
        {
            return new AiCommandOutcome(false, ex.Rejection.Code);
        }
    }
}

/// <summary>
/// Every <c>TickSeconds</c> (real time): polls presence, samples appliance power into the energy tracker, fires due comfort questions
/// and runs accepted routines. Disabled with <c>CeliknazHome:RunBackgroundJobs=false</c> (tests).
/// </summary>
internal sealed partial class CeliknazTicker(
    IServiceScopeFactory scopes,
    PresenceService presence,
    EnergyTracker energy,
    PresenceStore presenceStore,
    HomeDirectory directory,
    ComfortCheckScheduler comfortChecks,
    RoutineScheduler routines,
    IOptions<SimulationOptions> simulation,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<CeliknazTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("CeliknazHome:RunBackgroundJobs", true))
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(simulation.Value.TickSeconds), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunAsync("presence", () => presence.PollAsync(stoppingToken));
            await RunAsync("energy", () => SampleEnergyAsync(stoppingToken));
            await RunAsync("comfort checks", () => comfortChecks.ProcessDueAsync(stoppingToken));
            await RunAsync("routines", () => routines.ProcessDueAsync(stoppingToken));
        }
    }

    private async Task SampleEnergyAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var homeWhiz = scope.ServiceProvider.GetRequiredService<IHomeWhizClient>();
        var appliances = await homeWhiz.GetAppliancesAsync(DemoHome.HomeId, cancellationToken);
        if (appliances is null)
        {
            return;
        }

        var members = await directory.MemberIdsAsync(cancellationToken);
        energy.RecordSample(appliances, members.All(m => presenceStore.Band(m) == PresenceBands.Away));
    }

    private async Task RunAsync(string job, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJobFailed(logger, ex, job);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Çeliknaz background job {Job} failed")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, string job);
}

/// <summary>Runs <see cref="PatternLearner"/> shortly after start and then hourly.</summary>
internal sealed partial class PatternLearnerService(
    PatternLearner learner,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<PatternLearnerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("CeliknazHome:RunBackgroundJobs", true))
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), timeProvider);
        do
        {
            try
            {
                await learner.RunAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pattern learner run failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}

public static class CeliknazInfrastructureExtensions
{
    /// <summary>Registers every slice 2 service, option and background job. Called from <c>AddCeliknazHome</c> in the Api project.</summary>
    public static IServiceCollection AddCeliknazHomeServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddOptions<EnergyOptions>(services, configuration, EnergyOptions.SectionName);
        AddOptions<InfraCostOptions>(services, configuration, InfraCostOptions.SectionName);
        AddOptions<PresenceOptions>(services, configuration, PresenceOptions.SectionName);
        AddOptions<ComfortOptions>(services, configuration, ComfortOptions.SectionName);
        services.AddSingleton<IValidateOptions<EnergyOptions>, EnergyOptionsValidator>();

        services.AddSingleton<IHomeClock, SimulationHomeClock>();
        services.AddSingleton(sp => new EnergyModel(sp.GetRequiredService<IOptions<EnergyOptions>>().Value));
        services.AddSingleton<ComfortPolicy>();

        services.AddSingleton<HomeDirectory>();
        services.AddSingleton<MemberProfileStore>();
        services.AddSingleton<PresenceStore>();
        services.AddSingleton<Application.Settings.SettingsService>();
        services.AddSingleton<Application.Settings.HomeContextService>();
        services.AddSingleton<Application.Notifications.NotificationService>();
        services.AddSingleton<ComfortService>();
        services.AddSingleton<ComfortFeedbackService>();
        services.AddSingleton<PatternLearner>();
        services.AddSingleton<LearningService>();
        services.AddSingleton<RoutineScheduler>();
        services.AddSingleton<HistorySeeder>();
        services.AddSingleton<EnergyTracker>();
        services.AddSingleton<CheapestTimeService>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<Application.Dashboard.DashboardService>();
        services.AddSingleton<MockLocationService>();
        services.AddSingleton<ILocationSource>(sp => sp.GetRequiredService<MockLocationService>());
        services.AddSingleton<IMockLocationControl>(sp => sp.GetRequiredService<MockLocationService>());
        services.AddSingleton<PresenceService>();
        services.AddSingleton<ICeliknazHomeFacade, CeliknazHomeFacade>();
        services.AddSingleton<IAiCommandSender, AiCommandSender>();

        services.AddSingleton<ComfortCheckScheduler>();
        services.AddSingleton<OverrideDetector>();
        services.AddSingleton<IDeviceCommandObserver>(sp => sp.GetRequiredService<ComfortCheckScheduler>());
        services.AddSingleton<IDeviceCommandObserver>(sp => sp.GetRequiredService<OverrideDetector>());
        services.AddSingleton<IDeviceCommandObserver>(sp => sp.GetRequiredService<EnergyTracker>());
        services.AddSingleton<IApplianceEventObserver>(sp => sp.GetRequiredService<TaskService>());

        services.AddSingleton<IDeviceEventReader, Persistence.Mongo.MongoDeviceEventReader>();
        services.AddSingleton<IComfortFeedbackStore, Persistence.Mongo.MongoComfortFeedbackStore>();
        services.AddSingleton<Application.Dashboard.IRunStatsReader, Persistence.Mongo.MongoRunStatsReader>();

        services.AddHostedService<CeliknazTicker>();
        services.AddHostedService<PatternLearnerService>();
        return services;
    }

    private static void AddOptions<TOptions>(IServiceCollection services, IConfiguration configuration, string section)
        where TOptions : class =>
        services.AddOptions<TOptions>().Bind(configuration.GetSection(section)).ValidateDataAnnotations().ValidateOnStart();
}
