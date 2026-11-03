using Beko.Agents.Domain.Homes;

namespace Beko.Agents.Infrastructure.HomeWhiz.Mock;

/// <summary>The seeded demo home with stable ids from the contract.</summary>
public static class DemoHomeSeed
{
    public const string HomeId = "home-1";

    public static readonly Home Home = new(HomeId, "Evim", "İstanbul", new GeoLocation(41.0082, 28.9784));

    public static readonly IReadOnlyList<Room> Rooms =
    [
        new("salon", "Salon"),
        new("yatak-odasi", "Yatak Odası"),
        new("cocuk-odasi", "Çocuk Odası"),
        new("mutfak", "Mutfak"),
        new("camasir-odasi", "Çamaşır Odası"),
    ];

    public static readonly IReadOnlyList<HouseholdMember> Members =
    [
        new("ayse", "Ayşe", "owner"),
        new("mehmet", "Mehmet", "member"),
        new("zeynep", "Zeynep", "member"),
    ];

    private static readonly ApplianceCapabilities AcCapabilities = new(
        [CommandTypes.Power, CommandTypes.Setpoint, CommandTypes.Mode, CommandTypes.Fan],
        ["cool", "heat", "dry", "fan", "auto", "eco"],
        ["auto", "low", "medium", "high"],
        new SetpointRange(16, 30, 0.5),
        []);

    private static readonly ApplianceCapabilities WasherCapabilities = new(
        [CommandTypes.Power, CommandTypes.StartProgram, CommandTypes.Pause, CommandTypes.Resume, CommandTypes.Stop],
        [],
        [],
        null,
        [.. ProgramCatalog.WashingMachine.Select(p => p.Name)]);

    private static readonly ApplianceCapabilities DishwasherCapabilities = WasherCapabilities with
    {
        Programs = [.. ProgramCatalog.Dishwasher.Select(p => p.Name)],
    };

    private static readonly ApplianceCapabilities OvenCapabilities = new(
        [CommandTypes.Power, CommandTypes.Mode, CommandTypes.SetOven, CommandTypes.Pause, CommandTypes.Resume, CommandTypes.Stop],
        ["conventional", "fan", "grill"],
        [],
        new SetpointRange(50, 250, 5),
        []);

    private static readonly ApplianceCapabilities FridgeCapabilities = new([], [], [], null, []);

    /// <summary>Creates the initial appliances with <paramref name="nowUtc"/> as their update time.</summary>
    public static IReadOnlyList<Appliance> CreateAppliances(DateTime nowUtc) =>
    [
        Ac("ac-salon", "salon", "Salon Kliması", "Beko BK-12 (demo)", nowUtc, power: true, setpoint: 25, roomTempC: 24.8, humidityPct: 55),
        Ac("ac-yatak", "yatak-odasi", "Yatak Odası Kliması", "Beko BK-09 (demo)", nowUtc, power: false, setpoint: 24, roomTempC: 27.5, humidityPct: 58),
        Ac("ac-cocuk", "cocuk-odasi", "Çocuk Odası Kliması", "Beko BK-09 (demo)", nowUtc, power: false, setpoint: 23, roomTempC: 27, humidityPct: 57),
        new Appliance(
            "dishwasher-1",
            HomeId,
            "mutfak",
            ApplianceTypes.Dishwasher,
            "Bulaşık Makinesi",
            "Beko BDF-1500 (demo)",
            DishwasherCapabilities,
            new ApplianceState(true, ApplianceStatuses.Idle, 0, nowUtc, null, new ProgramState("eco50", 50, 180, 0, null), null, null)),
        new Appliance(
            "oven-1",
            HomeId,
            "mutfak",
            ApplianceTypes.Oven,
            "Fırın",
            "Beko BBIM-13300 (demo)",
            OvenCapabilities,
            new ApplianceState(false, ApplianceStatuses.Off, 0, nowUtc, null, null, new OvenState("conventional", 180, ApplianceSimulator.KitchenTempC, 0), null)),
        new Appliance(
            "fridge-1",
            HomeId,
            "mutfak",
            ApplianceTypes.Refrigerator,
            "Buzdolabı",
            "Beko RCNA-406 (demo)",
            FridgeCapabilities,
            new ApplianceState(true, ApplianceStatuses.Running, ApplianceSimulator.FridgePowerW, nowUtc, null, null, null, new FridgeState(4, -18, false))),
        new Appliance(
            "washer-1",
            HomeId,
            "camasir-odasi",
            ApplianceTypes.WashingMachine,
            "Çamaşır Makinesi",
            "Beko WTV-9612 (demo)",
            WasherCapabilities,
            new ApplianceState(true, ApplianceStatuses.Running, 450, nowUtc, null, new ProgramState("cotton40", 40, 42, 65, null), null, null)),
    ];

    private static Appliance Ac(string id, string roomId, string displayName, string model, DateTime nowUtc, bool power, double setpoint, double roomTempC, double humidityPct)
    {
        var ac = new AcState(setpoint, "cool", "auto", roomTempC, humidityPct);
        var state = new ApplianceState(
            power,
            power ? ApplianceStatuses.Running : ApplianceStatuses.Off,
            ApplianceSimulator.AcPowerW(power, ac),
            nowUtc,
            ac,
            null,
            null,
            null);
        return new Appliance(id, HomeId, roomId, ApplianceTypes.AirConditioner, displayName, model, AcCapabilities, state);
    }
}
