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

        Return ONLY this JSON object:
        {
          "summary": string,
          "recommendation": "build" | "buy" | "neutral",
          "sectionReasoning": { "<exact section name>": string, ... },
          "reasoning": string[]
        }

        Use the EXACT section names from the structured comparison as the keys of sectionReasoning.
        Choose "neutral" only when Build and Buy are within ~10% on 3-year TCO.

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
    }
}
