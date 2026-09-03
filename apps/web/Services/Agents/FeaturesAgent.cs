using System.Text.Json.Serialization;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Foundry agent for the Scope tab's Features step (Background → Requirements → Features).
/// </summary>
public sealed class FeaturesAgent : BaseFoundryAgent
{
    public FeaturesAgent(FoundryOptions options, ILogger<FeaturesAgent> logger)
        : base(options, logger, AgentInstructions.Features)
    {
    }

    protected override string AgentNameSuffix => "features-agent";

    public async Task<FeatureSet> RunAsync(string corpus, ScopeSummary scope, List<TechnicalRequirement> requirements, CancellationToken ct)
    {
        var agent = CreateAgent();
        var wrapper = await RunJsonAsync<FeaturesWrapper>(agent, FeaturesPrompt(corpus, scope, requirements), ct);
        if (wrapper is null)
            throw new InvalidOperationException("Features step returned no JSON.");

        return FeatureSetFromWrapper(wrapper);
    }

    private string FeaturesPrompt(string corpus, ScopeSummary scope, List<TechnicalRequirement> requirements) =>
        $$"""
        {{StepInstruction.Instructions}}

        Given this SCOPE and these REQUIREMENTS, derive a prioritized feature list for the solution.

        SCOPE: {{Serialize(scope)}}

        REQUIREMENTS: {{Serialize(requirements)}}

        Return JSON: { "features": [ {
          "name": string,
          "description": string,
          "category": "Core|Enhancement|Integration|Admin",
          "priority": "Must|Should|Could"
        } ] }

        6-12 features covering the core capability plus supporting/admin/integration features.

        DOCUMENTS:
        {{corpus}}
        """;

    private static FeatureSet FeatureSetFromWrapper(FeaturesWrapper wrapper)
    {
        var set = new FeatureSet
        {
            Notes = { "Feature list proposed by Microsoft Foundry prompt agent; review before committing to a build plan." }
        };

        foreach (var item in wrapper.Features)
        {
            set.Features.Add(new FeatureItem
            {
                Name = string.IsNullOrWhiteSpace(item.Name) ? "Untitled feature" : item.Name,
                Description = item.Description,
                Category = string.IsNullOrWhiteSpace(item.Category) ? "Core" : item.Category,
                Priority = string.IsNullOrWhiteSpace(item.Priority) ? "Should" : item.Priority
            });
        }

        return set;
    }

    private sealed class FeaturesWrapper
    {
        [JsonPropertyName("features")]
        public List<FeatureWrapperItem> Features { get; set; } = new();
    }

    private sealed class FeatureWrapperItem
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("priority")] public string Priority { get; set; } = "";
    }
}
