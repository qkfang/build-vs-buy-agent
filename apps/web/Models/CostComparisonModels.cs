namespace Proj37.CostEstimator.Web.Models;

/// <summary>
/// The result of the Compare step: a Build-vs-Buy cost comparison between the agentic Azure "build"
/// estimate (produced by the estimation pipeline) and the off-the-shelf "buy" baseline extracted from
/// the source document's cost section. The numeric analysis is deterministic and auditable; the Compare
/// agent enriches it with a narrative summary, a recommendation, and per-section reasoning.
/// </summary>
public sealed class CostComparison
{
    public string JobId { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Whether the reasoning was written by the live Foundry agent or the deterministic offline fallback.</summary>
    public string Engine { get; set; } = "offline";        // foundry | offline

    /// <summary>Reporting currency for all figures (USD).</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>True when a "buy" / off-the-shelf cost section was found in the source documents.</summary>
    public bool BuyCostAvailable { get; set; }

    /// <summary>The agent's overall verdict.</summary>
    public string Summary { get; set; } = "";

    /// <summary>build | buy | neutral.</summary>
    public string Recommendation { get; set; } = "neutral";

    public List<CostComparisonSection> Sections { get; set; } = new();
    public ComparisonTotals Totals { get; set; } = new();

    /// <summary>Overall reasoning bullet points behind the recommendation.</summary>
    public List<string> Reasoning { get; set; } = new();

    public List<string> Notes { get; set; } = new();
}

/// <summary>A single cost dimension compared across the Build and Buy options.</summary>
public sealed class CostComparisonSection
{
    public string Section { get; set; } = "";              // e.g. "Solution setup & build (one-time)"
    public string CostType { get; set; } = "";             // "One-time" | "Annual (recurring)"

    public decimal BuildCost { get; set; }
    public string BuildDetail { get; set; } = "";          // what makes up the build number

    public decimal BuyCost { get; set; }
    public string BuyDetail { get; set; } = "";            // what makes up the buy number

    /// <summary>Section-level reasoning (agent-written, or a deterministic template offline).</summary>
    public string Reasoning { get; set; } = "";

    /// <summary>Positive => Build is cheaper for this section (Buy − Build).</summary>
    public decimal Difference => Math.Round(BuyCost - BuildCost, 2);

    /// <summary>"build" | "buy" | "n/a" — which option is cheaper for this section.</summary>
    public string Cheaper =>
        BuildCost == 0 && BuyCost == 0 ? "n/a" : (BuildCost <= BuyCost ? "build" : "buy");
}

/// <summary>Roll-up totals for the Build and Buy options, in USD.</summary>
public sealed class ComparisonTotals
{
    // ---- Build (agentic Azure estimate) ----
    public decimal BuildOneTime { get; set; }              // one-time delivery/build cost
    public decimal BuildAnnualRecurring { get; set; }      // Azure infra + run/support per year
    public decimal BuildYearOne => Math.Round(BuildOneTime + BuildAnnualRecurring, 2);
    public decimal BuildThreeYearTco => Math.Round(BuildOneTime + 3 * BuildAnnualRecurring, 2);

    // ---- Buy (off-the-shelf / SaaS baseline) ----
    public decimal BuyOneTime { get; set; }
    public decimal BuyAnnualRecurring { get; set; }
    public decimal BuyYearOne => Math.Round(BuyOneTime + BuyAnnualRecurring, 2);
    public decimal BuyThreeYearTco => Math.Round(BuyOneTime + 3 * BuyAnnualRecurring, 2);
}
