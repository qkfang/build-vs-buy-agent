using System.Text.Json.Serialization;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Foundry agent for the Buy tab's Purchase step. Extracts every one-time and recurring cost to
/// purchase the off-the-shelf solution from the uploaded Buy documents plus the Spec summary.
/// </summary>
public sealed class PurchaseAgent : BaseFoundryAgent
{
    public PurchaseAgent(FoundryOptions options, ILogger<PurchaseAgent> logger)
        : base(options, logger, AgentInstructions.Purchase)
    {
    }

    protected override string AgentNameSuffix => "purchase-agent";

    public async Task<PurchaseCost> RunAsync(string buyCorpus, BuySpecSummary? spec, ScopeSummary? scope, CancellationToken ct)
    {
        var agent = CreateAgent();
        var plan = await RunJsonAsync<PurchasePlan>(agent, PurchasePrompt(buyCorpus, spec, scope), ct);
        if (plan is null)
            throw new InvalidOperationException("Purchase step returned no JSON.");

        return PurchaseCostFromPlan(plan);
    }

    private string PurchasePrompt(string buyCorpus, BuySpecSummary? spec, ScopeSummary? scope) =>
        $$"""
        {{StepInstruction.Instructions}}

        Extract ALL costs to purchase this off-the-shelf solution (one-time and recurring). You choose
        the line items and sizing; do NOT compute dollar totals (we multiply quantity × unit price).

        SPEC: {{Serialize(spec ?? new BuySpecSummary())}}
        SCOPE: {{Serialize(scope ?? new ScopeSummary())}}

        Return JSON: { "items": [ {
          "item": string,
          "description": string,
          "category": "License|Subscription|Implementation|Migration|Integration|Training|Accreditation",
          "cadence": "One-time|Monthly|Annual",
          "quantity": number,
          "unitPrice": number,
          "unit": string
        } ],
          "contingencyPercent": number
        }

        Always separate one-time onboarding/implementation costs from recurring licensing/subscription
        costs via the cadence field. 4-10 line items.

        BUY DOCUMENTS:
        {{buyCorpus}}
        """;

    private static PurchaseCost PurchaseCostFromPlan(PurchasePlan plan)
    {
        var estimate = new PurchaseCost
        {
            Currency = AzurePricingCatalog.Currency,
            ContingencyPercent = plan.ContingencyPercent is >= 5 and <= 30 ? plan.ContingencyPercent : 10m,
            Notes =
            {
                "Purchase plan extracted by Microsoft Foundry prompt agent from the uploaded Buy documents.",
                "One-time costs (onboarding/implementation) and recurring costs (licensing/subscription) are separated via cadence.",
                "Edit quantities / unit prices to adjust; validate against an actual vendor quote."
            }
        };

        foreach (var item in plan.Items)
        {
            var cadence = item.Cadence?.Trim() is "Monthly" or "Annual" ? item.Cadence!.Trim() : "One-time";
            estimate.Items.Add(new PurchaseCostLineItem
            {
                Item = string.IsNullOrWhiteSpace(item.Item) ? "Purchase line item" : item.Item,
                Description = item.Description,
                Category = string.IsNullOrWhiteSpace(item.Category) ? "License" : item.Category,
                Cadence = cadence,
                Quantity = item.Quantity > 0 ? item.Quantity : 1m,
                UnitPrice = item.UnitPrice > 0 ? item.UnitPrice : 0m,
                Unit = string.IsNullOrWhiteSpace(item.Unit) ? "per unit" : item.Unit
            });
        }

        if (estimate.Items.Count == 0)
        {
            estimate.Notes.Add("No purchase costs were found in the uploaded Buy documents; upload vendor pricing to populate this step.");
        }

        return estimate;
    }

    private sealed class PurchasePlan
    {
        [JsonPropertyName("items")]
        public List<PurchasePlanItem> Items { get; set; } = new();

        [JsonPropertyName("contingencyPercent")]
        public decimal ContingencyPercent { get; set; } = 10m;
    }

    private sealed class PurchasePlanItem
    {
        [JsonPropertyName("item")] public string Item { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("cadence")] public string? Cadence { get; set; }
        [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
        [JsonPropertyName("unitPrice")] public decimal UnitPrice { get; set; }
        [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    }
}
