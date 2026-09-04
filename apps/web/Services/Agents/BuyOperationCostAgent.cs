using System.Text.Json.Serialization;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Foundry agent for the Buy tab's Operation Cost step. Estimates the ongoing monthly cost to run the
/// purchased (Buy) solution, extracting detail from the uploaded Buy documents plus the Scope step.
/// </summary>
public sealed class BuyOperationCostAgent : BaseFoundryAgent
{
    public BuyOperationCostAgent(FoundryOptions options, ILogger<BuyOperationCostAgent> logger)
        : base(options, logger, AgentInstructions.BuyOperations)
    {
    }

    protected override string AgentNameSuffix => "buy-operation-cost-agent";

    public async Task<OperationCost> RunAsync(string buyCorpus, BuySpecSummary? spec, ScopeSummary? scope, CancellationToken ct)
    {
        var agent = CreateAgent();
        var plan = await RunJsonAsync<BuyOperationsPlan>(agent, OperationsPrompt(buyCorpus, spec, scope), ct);
        if (plan is null)
            throw new InvalidOperationException("Operation Cost (Buy) step returned no JSON.");

        return OperationsFromPlan(plan);
    }

    private string OperationsPrompt(string buyCorpus, BuySpecSummary? spec, ScopeSummary? scope) =>
        $$"""
        {{StepInstruction.Instructions}}

        Estimate the ONGOING monthly cost to run, support, and administer this BOUGHT solution after
        go-live (separate from the one-time Purchase cost). You choose the activities and sizing; do NOT
        compute dollar totals (we multiply quantity × unit price).

        SPEC: {{Serialize(spec ?? new BuySpecSummary())}}
        SCOPE: {{Serialize(scope ?? new ScopeSummary())}}

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

        Include vendor support/maintenance fees not already in the Purchase step's recurring costs, plus
        internal vendor/user administration effort. Add security & compliance reviews when data is
        PII/regulated. 3-6 line items.

        BUY DOCUMENTS:
        {{buyCorpus}}
        """;

    private static OperationCost OperationsFromPlan(BuyOperationsPlan plan)
    {
        var estimate = new OperationCost
        {
            Currency = AzurePricingCatalog.Currency,
            ContingencyPercent = plan.ContingencyPercent is >= 5 and <= 40 ? plan.ContingencyPercent : 15m,
            Notes =
            {
                "Buy-option operating plan proposed by Microsoft Foundry prompt agent from the uploaded Buy documents.",
                "Ongoing monthly cost to run, support, and administer the purchased solution (excludes the one-time Purchase cost).",
                "Edit quantities / unit prices to adjust the operating model; validate against an actual vendor support agreement."
            }
        };

        foreach (var item in plan.Items)
        {
            estimate.Items.Add(new OperationCostLineItem
            {
                Item = string.IsNullOrWhiteSpace(item.Item) ? "Vendor operating activity" : item.Item,
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
            estimate.Notes.Add("No Buy documents were found to derive an operating model; supplementing with a baseline vendor-support model.");
            estimate.Items.Add(new OperationCostLineItem { Item = "Vendor support & SLA management", Description = "Vendor liaison, ticket triage, and SLA tracking.", Category = "Support", Cadence = "Monthly", Quantity = 8m, UnitPrice = 120m, Unit = "per hour" });
            estimate.Items.Add(new OperationCostLineItem { Item = "User & access administration", Description = "Seat/licence provisioning and access reviews.", Category = "Operations", Cadence = "Monthly", Quantity = 6m, UnitPrice = 110m, Unit = "per hour" });
        }

        return estimate;
    }

    private sealed class BuyOperationsPlan
    {
        [JsonPropertyName("items")]
        public List<BuyOperationsPlanItem> Items { get; set; } = new();

        [JsonPropertyName("contingencyPercent")]
        public decimal ContingencyPercent { get; set; } = 15m;
    }

    private sealed class BuyOperationsPlanItem
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
