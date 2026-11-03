using System.ComponentModel.DataAnnotations;

namespace Beko.Agents.Application.Options;

/// <summary>Server cost assumptions for the Arçelik dashboard (section <c>InfraCost</c>).</summary>
public sealed class InfraCostOptions
{
    public const string SectionName = "InfraCost";

    /// <summary>Real or "equivalent" monthly cost of the demo server in TL.</summary>
    [Range(0, 100_000_000)]
    public double MonthlyServerCostTl { get; set; } = 1_500;

    /// <summary>Homes one server of this size could serve; scales the server cost in the fleet projection.</summary>
    [Range(1, 10_000_000)]
    public int HomesPerServer { get; set; } = 5_000;
}

/// <summary>Geofence, mock route and pre-conditioning settings (section <c>Presence</c>).</summary>
public sealed class PresenceOptions
{
    public const string SectionName = "Presence";

    /// <summary>Closer than this to the home means the member is <c>home</c>.</summary>
    [Range(10, 5_000)]
    public double HomeRadiusM { get; set; } = 300;

    /// <summary>Closer than this (and not home) means <c>near</c>; farther means <c>away</c>.</summary>
    [Range(100, 50_000)]
    public double NearRadiusM { get; set; } = 2_000;

    /// <summary>Hysteresis around both radii so a member on the border does not flap.</summary>
    [Range(0, 1_000)]
    public double HysteresisM { get; set; } = 50;

    /// <summary>Pre-conditioning lead time for the <c>balanced</c> mode.</summary>
    [Range(1, 240)]
    public double PreConditionMinutesBalanced { get; set; } = 20;

    /// <summary>Pre-conditioning lead time for the <c>comfort</c> mode (earlier).</summary>
    [Range(1, 240)]
    public double PreConditionMinutesComfort { get; set; } = 30;

    /// <summary>Travel speed of the scripted mock routes.</summary>
    [Range(1, 200)]
    public double TravelSpeedKmh { get; set; } = 30;

    /// <summary>How far from home <c>commute_out</c> ends and <c>commute_back</c> starts.</summary>
    [Range(1_000, 100_000)]
    public double FarDistanceM { get; set; } = 10_000;
}

/// <summary>Comfort engine knobs (section <c>Comfort</c>).</summary>
public sealed class ComfortOptions
{
    public const string SectionName = "Comfort";

    /// <summary>Outdoor humidity until a weather mock exists.</summary>
    [Range(0, 100)]
    public double OutdoorHumidityPct { get; set; } = 60;

    /// <summary>Dry mode is only tried at or above this indoor humidity.</summary>
    [Range(30, 100)]
    public double DryHumidityThresholdPct { get; set; } = 60;

    /// <summary>Simulated minutes between an AI-sourced command and the comfort question.</summary>
    [Range(1, 240)]
    public double CheckDelaySimMinutes { get; set; } = 20;
}
