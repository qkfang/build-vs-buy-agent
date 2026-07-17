using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// A pluggable estimation engine. The app ships two implementations:
///  - <c>OfflineEstimationEngine</c>: deterministic, signal-based, always available.
///  - <c>FoundryEstimationEngine</c>: uses a Microsoft Foundry prompt agent for the reasoning steps,
///    falling back to the offline engine if Foundry is unavailable.
/// </summary>
public interface IEstimationEngine
{
    string Name { get; }

    Task<EstimationResult> EstimateAsync(
        EstimationResult job,
        CancellationToken ct = default);
}
