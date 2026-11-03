using System.Runtime.CompilerServices;

namespace Beko.Agents.Api.IntegrationTests;

internal static class TestEnvironment
{
    /// <summary>
    /// The app applies EF migrations on startup in Development; the tests have no PostgreSQL, so switch that off
    /// before any host is built (environment variables are part of the default configuration).
    /// </summary>
    [ModuleInitializer]
    internal static void DisableMigrationOnStartup() =>
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
}
