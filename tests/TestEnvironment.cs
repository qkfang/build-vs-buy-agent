using System.Runtime.CompilerServices;

namespace Proj37.CostEstimator.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Pins the test process to the deterministic offline engine. Environment variables outrank
    /// appsettings.json (which carries a live Foundry endpoint for local dev), so no test host
    /// provisions or calls real Foundry agents — regardless of host-build ordering under parallel runs.
    /// </summary>
    [ModuleInitializer]
    internal static void DisableFoundry() =>
        Environment.SetEnvironmentVariable("Foundry__ProjectEndpoint", string.Empty);
}
