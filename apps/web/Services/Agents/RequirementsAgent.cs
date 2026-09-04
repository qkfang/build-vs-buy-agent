using System.Text.Json.Serialization;
using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services.Foundry;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Foundry agent for the Requirements step.
/// </summary>
public sealed class RequirementsAgent : BaseFoundryAgent
{
    public RequirementsAgent(FoundryOptions options, FoundryAgentProvisioner provisioner, ILogger<RequirementsAgent> logger)
        : base(options, provisioner, logger, AgentInstructions.Requirements)
    {
    }

    protected override string AgentNameSuffix => "requirements-agent";

    public async Task<List<TechnicalRequirement>> RunAsync(string corpus, ScopeSummary scope, CancellationToken ct)
    {
        var agent = await GetAgentAsync(ct);
        var wrapper = await RunJsonAsync<RequirementsWrapper>(agent, RequirementsPrompt(corpus, scope), ct);
        var requirements = wrapper?.Requirements ?? throw new InvalidOperationException("Requirements step returned no JSON.");
        RenumberRequirements(requirements);
        return requirements;
    }

    private string RequirementsPrompt(string corpus, ScopeSummary scope) =>
        $$"""
        {{StepInstruction.Instructions}}

        Given this SCOPE and the source documents, derive the technical requirements for an Azure solution.

        SCOPE: {{Serialize(scope)}}

        Return JSON: { "requirements": [ {
          "id": "REQ-001",
          "category": "Compute|Data|Networking|Security|AI|Observability",
          "requirement": string,
          "rationale": string,
          "priority": "Must|Should|Could"
        } ] }

        Cover compute, data, AI/Foundry (if relevant), security (managed identity, Key Vault), networking
        (HTTPS-only), and observability. 8-14 requirements.

        DOCUMENTS:
        {{corpus}}
        """;

    private static void RenumberRequirements(List<TechnicalRequirement> requirements)
    {
        for (int i = 0; i < requirements.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(requirements[i].Id)) requirements[i].Id = $"REQ-{i + 1:000}";
            if (string.IsNullOrWhiteSpace(requirements[i].Priority)) requirements[i].Priority = "Should";
            if (string.IsNullOrWhiteSpace(requirements[i].Category)) requirements[i].Category = "Other";
        }
    }

    private sealed class RequirementsWrapper
    {
        [JsonPropertyName("requirements")]
        public List<TechnicalRequirement> Requirements { get; set; } = new();
    }
}
