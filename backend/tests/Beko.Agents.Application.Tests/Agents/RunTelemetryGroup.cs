namespace Beko.Agents.Application.Tests.Agents;

/// <summary>Tests that listen to the platform's global ActivitySource and Meter must not run in parallel.</summary>
[CollectionDefinition("RunTelemetry", DisableParallelization = true)]
public sealed class RunTelemetryGroup;
