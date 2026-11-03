using System.Diagnostics;
using Beko.Agents.Application.Common;
using Beko.Agents.Application.Homes;
using Beko.Agents.Application.Learning;
using Beko.Agents.Application.Options;
using Beko.Agents.Application.Settings;
using Microsoft.Extensions.Options;

namespace Beko.Agents.Application.Comfort;

/// <summary>Comfort question answers: stores them and updates the member's sensitivity offset and context bias.</summary>
public sealed class ComfortFeedbackService(
    ComfortService comfort,
    IComfortFeedbackStore store,
    MemberProfileStore profiles,
    SettingsService settings,
    HomeDirectory directory,
    IHomeClock clock,
    TimeProvider timeProvider)
{
    /// <summary>Returns <c>null</c> on success, otherwise a problem code: <c>invalid_sensation</c>, <c>invalid_clothing</c>, <c>unknown_member</c>, <c>not_found</c>, <c>not_air_conditioner</c>.</summary>
    public async Task<string?> SubmitAsync(
        string memberId,
        string applianceId,
        int sensation,
        string? clothing,
        string? commandId,
        CancellationToken cancellationToken)
    {
        if (sensation is < -1 or > 1)
        {
            return "invalid_sensation";
        }

        if (clothing is not null && !ClothingSetting.All.Contains(clothing))
        {
            return "invalid_clothing";
        }

        if (!await directory.IsMemberAsync(memberId, cancellationToken))
        {
            return "unknown_member";
        }

        var basis = await comfort.BasePmvAsync(applianceId, memberId, cancellationToken);
        if (basis is null)
        {
            return "not_found";
        }

        var (pmvBase, clo, roomTempC, humidityPct) = basis.Value;
        var current = Activity.Current;
        await store.AddAsync(
            new ComfortFeedbackRecord(
                Guid.NewGuid().ToString("N"),
                DemoHome.HomeId,
                memberId,
                applianceId,
                sensation,
                clothing,
                commandId,
                roomTempC,
                humidityPct,
                Math.Round(pmvBase, 3),
                clo,
                timeProvider.GetUtcNow().UtcDateTime,
                current?.TraceId.ToHexString(),
                current?.SpanId.ToHexString()),
            cancellationToken);

        profiles.SetOffset(memberId, SensitivityModel.Update(profiles.GetOffset(memberId), pmvBase, sensation));
        var key = ComfortService.ContextKey(clock.LocalNow());
        profiles.SetBias(memberId, key, SensitivityModel.UpdateBias(profiles.GetBias(memberId, key), sensation));

        if (clothing is not null)
        {
            settings.Update(memberId, new MemberSettingsUpdate(null, null, null, clothing, null));
        }

        return null;
    }
}

/// <summary>
/// Asks "Nasıl, rahat mı?" about 20 simulated minutes after an AI-sourced command (SignalR <c>ComfortCheckRequested</c>),
/// unless the member's notification level is <c>quiet</c>. Hooked into the command pipeline as an observer.
/// </summary>
public sealed class ComfortCheckScheduler(
    IHomeClock clock,
    SettingsService settings,
    IHomeEventPublisher publisher,
    IOptions<ComfortOptions> options) : IDeviceCommandObserver
{
    private readonly Lock _gate = new();
    private readonly List<PendingCheck> _pending = [];

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public Task OnCommandAsync(DeviceEventRecord deviceEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceEvent);
        if (deviceEvent.Source != "ai" || deviceEvent.After.Ac is null)
        {
            return Task.CompletedTask;
        }

        var due = clock.UtcNow.AddMinutes(options.Value.CheckDelaySimMinutes);
        lock (_gate)
        {
            _pending.RemoveAll(p => p.ApplianceId == deviceEvent.DeviceId && p.MemberId == deviceEvent.MemberId);
            _pending.Add(new PendingCheck(deviceEvent.DeviceId, deviceEvent.MemberId, deviceEvent.CommandId, due));
        }

        return Task.CompletedTask;
    }

    /// <summary>Publishes the questions that are due in simulated time.</summary>
    public async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        List<PendingCheck> due;
        lock (_gate)
        {
            var now = clock.UtcNow;
            due = [.. _pending.Where(p => p.DueUtc <= now)];
            _pending.RemoveAll(p => p.DueUtc <= now);
        }

        foreach (var check in due.Where(c => settings.Get(c.MemberId).NotificationLevel != NotificationLevelSetting.Quiet))
        {
            await publisher.ComfortCheckRequestedAsync(DemoHome.HomeId, check.ApplianceId, check.MemberId, check.CommandId, cancellationToken);
        }
    }

    private sealed record PendingCheck(string ApplianceId, string MemberId, string CommandId, DateTime DueUtc);
}

/// <summary>
/// Implicit feedback: a user command (app/remote/voice) shortly after an AI command on the same device is an override;
/// the learner is run for the member right away so the bias shows up in the next decision.
/// </summary>
public sealed class OverrideDetector(IHomeClock clock, PatternLearner learner, TimeProvider timeProvider) : IDeviceCommandObserver
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTime AtUtc, string MemberId)> _lastAi = new(StringComparer.Ordinal);

    public async Task OnCommandAsync(DeviceEventRecord deviceEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceEvent);
        string? aiMember = null;
        lock (_gate)
        {
            if (deviceEvent.Source == "ai")
            {
                _lastAi[deviceEvent.DeviceId] = (timeProvider.GetUtcNow().UtcDateTime, deviceEvent.MemberId);
            }
            else if (deviceEvent.Source is "app" or "remote" or "voice" && _lastAi.Remove(deviceEvent.DeviceId, out var ai))
            {
                var simMinutes = (timeProvider.GetUtcNow().UtcDateTime - ai.AtUtc).TotalMinutes * clock.TimeScale;
                if (simMinutes <= PatternLearner.OverrideWindowSimMinutes)
                {
                    aiMember = ai.MemberId;
                }
            }
        }

        if (aiMember is not null)
        {
            await learner.RunAsync(aiMember, cancellationToken);
        }
    }
}
