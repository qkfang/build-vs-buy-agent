using System.Text.Json.Serialization;
using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services.Foundry;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Foundry agent for the Compare step.
/// </summary>
public sealed class CompareAgent : BaseFoundryAgent
{
    public CompareAgent(FoundryOptions options, FoundryAgentProvisioner provisioner, ILogger<CompareAgent> logger)
        : base(options, provisioner, logger, AgentInstructions.Compare)
    {
    }

    protected override string AgentNameSuffix => "compare-agent";

    public async Task<CostComparison> RunAsync(EstimationResult job, CostComparison comparison, CancellationToken ct)
    {
        var agent = await GetAgentAsync(ct);
        var narrative = await RunJsonAsync<AgentNarrative>(agent, ComparePrompt(job, comparison), ct);
        if (narrative is null)
            throw new InvalidOperationException("Compare step returned no JSON.");

        comparison.Summary = string.IsNullOrWhiteSpace(narrative.Summary) ? comparison.Summary : narrative.Summary.Trim();
        if (!string.IsNullOrWhiteSpace(narrative.Recommendation))
        {
            comparison.Recommendation = narrative.Recommendation.Trim().ToLowerInvariant() switch
            {
                "build" or "buy" or "neutral" => narrative.Recommendation.Trim().ToLowerInvariant(),
                _ => comparison.Recommendation
            };
        }

        if (narrative.Reasoning is { Count: > 0 })
            comparison.Reasoning = narrative.Reasoning;

        if (!string.IsNullOrWhiteSpace(narrative.PrimaryPlatform))
            comparison.PrimaryPlatform = narrative.PrimaryPlatform.Trim();

        if (narrative.Gates is { Count: > 0 })
            comparison.Gates = narrative.Gates;

        if (narrative.CommercialDrivers is { Count: > 0 })
            comparison.CommercialDrivers = narrative.CommercialDrivers;

        if (narrative.Sourcing is { Count: > 0 })
            comparison.Sourcing = narrative.Sourcing;

        if (narrative.SharedControls is { Count: > 0 })
            comparison.SharedControls = narrative.SharedControls;

        foreach (var section in comparison.Sections)
        {
            if (narrative.SectionReasoning is not null &&
                narrative.SectionReasoning.TryGetValue(section.Section, out var reasoning) &&
                !string.IsNullOrWhiteSpace(reasoning))
            {
                section.Reasoning = reasoning.Trim();
            }
        }

        foreach (var section in comparison.Sections.Where(s => string.IsNullOrWhiteSpace(s.Reasoning)))
        {
            var diff = Math.Abs(section.Difference);
            section.Reasoning = section.Cheaper == "n/a"
                ? "No comparable figure available for this section."
                : $"{(section.Cheaper == "build" ? "Build" : "Buy")} is cheaper here by {Money(diff)}.";
        }

        return comparison;
    }

    private string ComparePrompt(EstimationResult job, CostComparison comparison)
    {
        var structured = new
        {
            buyCostAvailable = comparison.BuyCostAvailable,
            totals = comparison.Totals,
            sections = comparison.Sections.Select(s => new
            {
                s.Section,
                s.CostType,
                s.BuildCost,
                s.BuildDetail,
                s.BuyCost,
                s.BuyDetail,
                s.Difference,
                s.Cheaper
            })
        };

        var corpus = string.Join(
            "\n\n",
            job.Documents.Select(d => $"=== FILE: {d.FileName} ===\n{(string.IsNullOrWhiteSpace(d.ExtractedText) ? d.Excerpt : d.ExtractedText)}"));
        corpus = Trunc(corpus, 24_000);

        return $$"""
        {{StepInstruction.Instructions}}

        Compare BUILDING this solution on Azure against BUYING an off-the-shelf product.
        The application has already computed the numbers below; do NOT recompute them.
        Explain and recommend based strictly on these figures and the source cost section.

        STRUCTURED COMPARISON:
        {{Serialize(structured)}}

        MANDATORY GATES (assess every one, in this order, for BOTH options):
        {{Serialize(BuildVsBuyFramework.MandatoryGates)}}

        COMMERCIAL DRIVER TAXONOMY (rate every one, using these exact driver names):
        {{Serialize(BuildVsBuyFramework.CommercialDrivers.Select(d => d.Driver))}}

        Return ONLY this JSON object:
        {
          "summary": string,
          "recommendation": "build" | "buy" | "neutral",
          "sectionReasoning": { "<exact section name>": string, ... },
          "reasoning": string[],
          "primaryPlatform": string,
          "gates": [ { "gate": string, "buildStatus": "pass|conditional|fail|unknown",
                       "buyStatus": "pass|conditional|fail|unknown", "note": string } ],
          "commercialDrivers": [ { "driver": string, "buildRating": "VH|H|M-H|M|L-M|L",
                                   "buyRating": "VH|H|M-H|M|L-M|L", "rationale": string,
                                   "sensitivity": string } ],
          "sourcing": [ { "capability": string, "choice": "Reuse|Buy|Configure|Extend|Build",
                          "rationale": string } ],
          "sharedControls": string[]
        }

        Use the EXACT section names from the structured comparison as the keys of sectionReasoning.
        Use the EXACT gate and driver names supplied above — one entry each, no additions or omissions.
        Mark a gate "unknown" when the documents are silent; never assume a pass.
        Choose "neutral" only when Build and Buy are within ~10% on 3-year TCO and neither has a gate
        advantage. An option that fails a gate must not be recommended.

        SOURCE DOCUMENTS (for context on what the buy price covers):
        {{corpus}}
        """;
    }

    private static string Money(decimal amount) =>
        "$" + amount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class AgentNarrative
    {
        [JsonPropertyName("summary")] public string Summary { get; set; } = "";
        [JsonPropertyName("recommendation")] public string Recommendation { get; set; } = "";
        [JsonPropertyName("sectionReasoning")] public Dictionary<string, string>? SectionReasoning { get; set; }
        [JsonPropertyName("reasoning")] public List<string> Reasoning { get; set; } = new();
        [JsonPropertyName("primaryPlatform")] public string PrimaryPlatform { get; set; } = "";
        [JsonPropertyName("gates")] public List<MandatoryGateCheck>? Gates { get; set; }
        [JsonPropertyName("commercialDrivers")] public List<CommercialDriverRating>? CommercialDrivers { get; set; }
        [JsonPropertyName("sourcing")] public List<CapabilitySourcingDecision>? Sourcing { get; set; }
        [JsonPropertyName("sharedControls")] public List<string>? SharedControls { get; set; }
    }
}
