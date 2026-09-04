namespace Proj37.CostEstimator.Web.Models;

/// <summary>
/// The result of the Compare step: a Build-vs-Buy cost comparison between the agentic Azure "build"
/// estimate (produced by the estimation pipeline) and the off-the-shelf "buy" baseline taken from the
/// Buy tab steps (Purchase + Operation Cost) when present, otherwise extracted from the source
/// document's cost section. The numeric analysis is deterministic and auditable; the Compare
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

    /// <summary>True when a "buy" baseline was available — from the Buy tab steps (Purchase / Operation Cost) or, failing that, a cost section in the source documents.</summary>
    public bool BuyCostAvailable { get; set; }

    /// <summary>The agent's overall verdict.</summary>
    public string Summary { get; set; } = "";

    /// <summary>build | buy | neutral.</summary>
    public string Recommendation { get; set; } = "neutral";

    public List<CostComparisonSection> Sections { get; set; } = new();
    public ComparisonTotals Totals { get; set; } = new();

    /// <summary>One line naming the platform the solution should be anchored on (decision part 1).</summary>
    public string PrimaryPlatform { get; set; } = "";

    /// <summary>
    /// Mandatory control gates evaluated per option. An option that fails a gate is not viable regardless
    /// of cost, so gates are assessed before the commercial comparison is acted on.
    /// </summary>
    public List<MandatoryGateCheck> Gates { get; set; } = new();

    /// <summary>
    /// Relative (VH/H/M/L) profile of the cost drivers that dominate a three-year TCO. Deliberately
    /// relative rather than dollar-valued — these are starting hypotheses to recalibrate against real
    /// volumes, existing licences, labour rates and supplier terms.
    /// </summary>
    public List<CommercialDriverRating> CommercialDrivers { get; set; } = new();

    /// <summary>Per-capability sourcing choice (decision part 2) — Reuse / Buy / Configure / Extend / Build.</summary>
    public List<CapabilitySourcingDecision> Sourcing { get; set; } = new();

    /// <summary>Controls that should be provided once enterprise-wide rather than rebuilt per solution (decision part 3).</summary>
    public List<string> SharedControls { get; set; } = new();

    /// <summary>Overall reasoning bullet points behind the recommendation.</summary>
    public List<string> Reasoning { get; set; } = new();

    public List<string> Notes { get; set; } = new();
}

/// <summary>A mandatory control gate assessed against both the Build and the Buy option.</summary>
public sealed class MandatoryGateCheck
{
    public string Gate { get; set; } = "";
    public string BuildStatus { get; set; } = "unknown";   // pass | conditional | fail | unknown
    public string BuyStatus { get; set; } = "unknown";
    public string Note { get; set; } = "";
}

/// <summary>A three-year commercial cost driver rated relatively for each option.</summary>
public sealed class CommercialDriverRating
{
    public string Driver { get; set; } = "";
    public string BuildRating { get; set; } = "";          // VH | H | M-H | M | L-M | L
    public string BuyRating { get; set; } = "";
    public string Rationale { get; set; } = "";

    /// <summary>The assumption that, if it changed, would move the rating.</summary>
    public string Sensitivity { get; set; } = "";
}

/// <summary>How one capability should be sourced.</summary>
public sealed class CapabilitySourcingDecision
{
    public string Capability { get; set; } = "";
    public string Choice { get; set; } = "";               // Reuse | Buy | Configure | Extend | Build
    public string Rationale { get; set; } = "";
}

/// <summary>
/// The shared build-vs-buy decision framework: the mandatory control gates every option must clear, and
/// the commercial drivers that dominate a three-year TCO. The default ratings are starting hypotheses
/// (an Azure/Foundry-anchored build versus a packaged vendor product), used to seed the deterministic
/// offline comparison and to give the Compare agent a fixed taxonomy to rate against.
/// </summary>
public static class BuildVsBuyFramework
{
    public static readonly IReadOnlyList<string> MandatoryGates = new[]
    {
        "Sensitive data and information barriers can be isolated",
        "Accountable humans retain reserved investment, risk, legal and compliance decisions",
        "Identity and source-system permissions are enforced through every agent and tool call",
        "Inputs, sources, versions, outputs, approvals, overrides and actions are traceable",
        "Approved knowledge is grounded and unverified information is declared",
        "Data residency, retention, deletion, legal hold and sharing obligations can be met",
        "High-impact actions support approval, rejection, timeout and reversal or compensation",
        "Quality, reliability, safety, drift and operational performance can be evaluated",
        "Production does not depend on an unapproved preview feature without an alternative route",
        "Data, configuration and operations can transition if the product or supplier changes"
    };

    public static readonly IReadOnlyList<CommercialDriverDefault> CommercialDrivers = new[]
    {
        new CommercialDriverDefault("Initial implementation effort", "H", "M"),
        new CommercialDriverDefault("Business application configuration", "H", "M"),
        new CommercialDriverDefault("Custom agent engineering", "H", "L-M"),
        new CommercialDriverDefault("Integration and data enablement", "H", "M-H"),
        new CommercialDriverDefault("Platform and user licensing", "M", "H-VH"),
        new CommercialDriverDefault("AI/model consumption", "M-H", "M-H"),
        new CommercialDriverDefault("Infrastructure and environments", "M-H", "L-M"),
        new CommercialDriverDefault("Governance, security and assurance", "H", "M-H"),
        new CommercialDriverDefault("Operations and specialist skills", "H", "M"),
        new CommercialDriverDefault("Change and release management", "H", "M"),
        new CommercialDriverDefault("Vendor dependency and exit", "M-H", "VH"),
        new CommercialDriverDefault("Commercial uncertainty", "M-H", "H")
    };

    public sealed record CommercialDriverDefault(string Driver, string BuildRating, string BuyRating);
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
