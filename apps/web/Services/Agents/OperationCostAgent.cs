using System.Text.Json.Serialization;
using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services.Foundry;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Foundry agent for the Operation Cost step.
/// </summary>
public sealed class OperationCostAgent : BaseFoundryAgent
{
    public OperationCostAgent(FoundryOptions options, FoundryAgentProvisioner provisioner, ILogger<OperationCostAgent> logger)
        : base(options, provisioner, logger, AgentInstructions.Operations)
    {
    }

    protected override string AgentNameSuffix => "operation-cost-agent";

    public async Task<OperationCost> RunAsync(string corpus, ScopeSummary scope, CancellationToken ct)
    {
        var agent = await GetAgentAsync(ct);
        var plan = await RunJsonAsync<OperationsPlan>(agent, OperationsPrompt(corpus, scope), ct);
        if (plan is null)
            throw new InvalidOperationException("Operation Cost step returned no JSON.");

        return OperationsFromPlan(plan);
    }

    private string OperationsPrompt(string corpus, ScopeSummary scope) =>
        $$"""
        {{StepInstruction.Instructions}}

        Estimate the ONGOING monthly cost to run, support, and maintain this solution after go-live
        (separate from Azure infrastructure and from the one-time build). You choose the activities and
        sizing; do NOT compute dollar totals (we multiply quantity × unit price).

        SCOPE: {{Serialize(scope)}}

        Return JSON: { "items": [ {
          "item": string,
          "description": string,
          "category": "Support|Maintenance|Operations|Licensing",
          "cadence": string,
          "quantity": number,
          "unitPrice": number,
          "unit": string
        } ],
          "contingencyPercent": number
        }

        Always include application support, monitoring & incident response, software updates & patching,
        and minor enhancements. Add security & compliance reviews when data is PII/regulated, and AI model
        monitoring / prompt tuning when the workload uses AI. 4-8 line items.

        DOCUMENTS:
        {{corpus}}
        """;

    private static OperationCost OperationsFromPlan(OperationsPlan plan)
    {
        var estimate = new OperationCost
        {
            Currency = AzurePricingCatalog.Currency,
            ContingencyPercent = plan.ContingencyPercent is >= 5 and <= 40 ? plan.ContingencyPercent : 15m,
            Notes =
            {
                "Operating plan proposed by Microsoft Foundry prompt agent; quantities and rates are reference estimates.",
                "Ongoing monthly cost to run, support, and maintain the solution (excludes Azure infra and the one-time build).",
                "Edit quantities / unit prices to adjust the operating model; validate against an actual support agreement."
            }
        };

        foreach (var item in plan.Items)
        {
            estimate.Items.Add(new OperationCostLineItem
            {
                Item = string.IsNullOrWhiteSpace(item.Item) ? "Operating activity" : item.Item,
                Description = item.Description,
                Category = string.IsNullOrWhiteSpace(item.Category) ? "Operations" : item.Category,
                Cadence = string.IsNullOrWhiteSpace(item.Cadence) ? "Monthly" : item.Cadence,
                Quantity = item.Quantity > 0 ? item.Quantity : 1m,
                UnitPrice = item.UnitPrice > 0 ? item.UnitPrice : 120m,
                Unit = string.IsNullOrWhiteSpace(item.Unit) ? "per hour" : item.Unit
            });
        }

        if (estimate.Items.Count == 0)
        {
            estimate.Notes.Add("Foundry returned no operating line items; supplementing with a baseline run-support model.");
            estimate.Items.Add(new OperationCostLineItem { Item = "Application support (L2/L3)", Description = "Triage, bug fixes, and user support.", Category = "Support", Cadence = "Monthly", Quantity = 16m, UnitPrice = 120m, Unit = "per hour" });
            estimate.Items.Add(new OperationCostLineItem { Item = "Monitoring & incident response", Description = "Health monitoring and incident handling.", Category = "Operations", Cadence = "Monthly", Quantity = 8m, UnitPrice = 130m, Unit = "per hour" });
            estimate.Items.Add(new OperationCostLineItem { Item = "Software updates & patching", Description = "Dependency updates and security patching.", Category = "Maintenance", Cadence = "Monthly", Quantity = 6m, UnitPrice = 120m, Unit = "per hour" });
        }

        return estimate;
    }

    private sealed class OperationsPlan
    {
        [JsonPropertyName("items")]
        public List<OperationsPlanItem> Items { get; set; } = new();

        [JsonPropertyName("contingencyPercent")]
        public decimal ContingencyPercent { get; set; } = 15m;
    }

    private sealed class OperationsPlanItem
    {
        [JsonPropertyName("item")] public string Item { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("cadence")] public string Cadence { get; set; } = "";
        [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
        [JsonPropertyName("unitPrice")] public decimal UnitPrice { get; set; }
        [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    }
}
