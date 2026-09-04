using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services.Foundry;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Foundry agent for the Scope step.
/// </summary>
public sealed class ScopeAgent : BaseFoundryAgent
{
    public ScopeAgent(FoundryOptions options, FoundryAgentProvisioner provisioner, ILogger<ScopeAgent> logger)
        : base(options, provisioner, logger, AgentInstructions.Scope)
    {
    }

    protected override string AgentNameSuffix => "scope-agent";

    public async Task<ScopeSummary?> RunAsync(string corpus, CancellationToken ct)
    {
        var agent = await GetAgentAsync(ct);
        var scope = await RunJsonAsync<ScopeSummary>(agent, ScopePrompt(corpus), ct);
        if (scope is null)
            throw new InvalidOperationException("Scope step returned no JSON.");

        NormalizeScope(scope);
        return scope;
    }

    private string ScopePrompt(string corpus) =>
        $$"""
        {{StepInstruction.Instructions}}

        Analyze the following technical document(s) and produce a SCOPE summary.

        Return JSON with exactly these fields:
        {
          "projectName": string,
          "overview": string,
          "businessGoal": string,
          "inScope": string[],
          "outOfScope": string[],
          "assumptions": string[],
          "workloadProfile": string,
          "expectedScale": string,
          "dataSensitivity": string,
          "environment": string
        }

        DOCUMENTS:
        {{corpus}}
        """;

    private static void NormalizeScope(ScopeSummary scope)
    {
        scope.ProjectName = string.IsNullOrWhiteSpace(scope.ProjectName) ? "Untitled POC" : scope.ProjectName;
        scope.WorkloadProfile = string.IsNullOrWhiteSpace(scope.WorkloadProfile) ? "web workload" : scope.WorkloadProfile;
        scope.Environment = string.IsNullOrWhiteSpace(scope.Environment) ? "production" : scope.Environment;
    }
}
