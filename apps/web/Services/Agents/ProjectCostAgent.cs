using System.Text.Json.Serialization;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Foundry agent for the Project Cost step.
/// </summary>
public sealed class ProjectCostAgent : BaseFoundryAgent
{
    public ProjectCostAgent(FoundryOptions options, ILogger<ProjectCostAgent> logger)
        : base(options, logger, AgentInstructions.ProjectCost)
    {
    }

    protected override string AgentNameSuffix => "project-cost-agent";

    public async Task<ProjectBuildCost> RunAsync(string corpus, ScopeSummary scope, CancellationToken ct)
    {
        var agent = CreateAgent();
        var plan = await RunJsonAsync<BuildPlan>(agent, ProjectCostPrompt(corpus, scope), ct);
        if (plan is null)
            throw new InvalidOperationException("Project Cost step returned no JSON.");

        return ProjectCostFromPlan(plan);
    }

    private string ProjectCostPrompt(string corpus, ScopeSummary scope) =>
        $$"""
        {{StepInstruction.Instructions}}

        Plan the delivery team and effort to BUILD this solution (one-time cost). You choose the roles,
        their day rates, and person-days; do NOT compute dollar totals (we multiply rate × days).

        SCOPE: {{Serialize(scope)}}

        Return JSON: { "roles": [ {
          "role": string,
          "description": string,
          "dayRate": number,
          "estimatedDays": number
        } ],
          "contingencyPercent": number
        }

        Always include a Solution Architect, Project Manager, and QA Engineer. Add Backend, Frontend,
        AI/ML, Data, and DevOps roles only when the scope/requirements call for them. Scale the person-days
        to complexity and expected scale — a small POC is a few weeks; an enterprise build is much larger.

        DOCUMENTS:
        {{corpus}}
        """;

    private static ProjectBuildCost ProjectCostFromPlan(BuildPlan plan)
    {
        var estimate = new ProjectBuildCost
        {
            Currency = AzurePricingCatalog.Currency,
            ContingencyPercent = plan.ContingencyPercent is >= 5 and <= 40 ? plan.ContingencyPercent : 15m,
            Notes =
            {
                "Delivery plan proposed by Microsoft Foundry prompt agent; day rates and effort are reference estimates.",
                "One-time cost to design and build the solution (delivery team effort), separate from Azure run cost.",
                "Edit day rates / person-days to re-plan the team; validate against an actual statement of work."
            }
        };

        foreach (var role in plan.Roles)
        {
            estimate.Roles.Add(new ProjectRoleLineItem
            {
                Role = string.IsNullOrWhiteSpace(role.Role) ? "Delivery role" : role.Role,
                Description = role.Description,
                DayRate = role.DayRate > 0 ? role.DayRate : 900m,
                EstimatedDays = role.EstimatedDays > 0 ? role.EstimatedDays : 5m
            });
        }

        if (estimate.Roles.Count == 0)
        {
            estimate.Notes.Add("Foundry returned no delivery roles; supplementing with a baseline core team.");
            estimate.Roles.Add(new ProjectRoleLineItem { Role = "Solution Architect", Description = "Solution design and Azure architecture.", DayRate = 1200m, EstimatedDays = 10m });
            estimate.Roles.Add(new ProjectRoleLineItem { Role = "Backend Developer", Description = "APIs, services, and integration.", DayRate = 900m, EstimatedDays = 25m });
            estimate.Roles.Add(new ProjectRoleLineItem { Role = "QA Engineer", Description = "Test planning and release verification.", DayRate = 750m, EstimatedDays = 15m });
            estimate.Roles.Add(new ProjectRoleLineItem { Role = "Project Manager", Description = "Delivery planning and coordination.", DayRate = 1000m, EstimatedDays = 14m });
        }

        return estimate;
    }

    private sealed class BuildPlan
    {
        [JsonPropertyName("roles")]
        public List<BuildPlanRole> Roles { get; set; } = new();

        [JsonPropertyName("contingencyPercent")]
        public decimal ContingencyPercent { get; set; } = 15m;
    }

    private sealed class BuildPlanRole
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("dayRate")] public decimal DayRate { get; set; }
        [JsonPropertyName("estimatedDays")] public decimal EstimatedDays { get; set; }
    }
}
