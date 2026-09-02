using System.Text.Json.Serialization;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Foundry agent for the Cost Model step.
/// </summary>
public sealed class CostModelAgent : BaseFoundryAgent
{
    private readonly CloudCatalogService _cloudCatalog;

    public CostModelAgent(FoundryOptions options, ILogger<CostModelAgent> logger, CloudCatalogService? cloudCatalog = null)
        : base(options, logger, AgentInstructions.Cost)
    {
        _cloudCatalog = cloudCatalog ?? new CloudCatalogService();
    }

    protected override string AgentNameSuffix => "cost-model-agent";

    public async Task<CostEstimate> RunAsync(string corpus, ScopeSummary scope, string cloudProvider, CancellationToken ct)
    {
        var agent = CreateAgent();
        var provider = CloudCatalogService.NormalizeProvider(cloudProvider);
        var plan = await RunJsonAsync<ServicePlan>(agent, ServicePlanPrompt(corpus, scope, provider), ct);
        if (plan is null)
            throw new InvalidOperationException("Cost Model step returned no JSON.");

        var estimate = CostFromPlan(plan, scope);
        _cloudCatalog.ApplyToEstimate(estimate, provider);
        return estimate;
    }

    private string ServicePlanPrompt(string corpus, ScopeSummary scope, string cloudProvider) =>
        $$"""
        {{StepInstruction.Instructions}}

        Design the concrete {{cloudProvider.ToUpperInvariant()}} service plan to run this workload for ONE month, then
        estimate quantities. You decide services/SKUs/quantities; do NOT compute dollar costs (we price them separately).

        SCOPE: {{Serialize(scope)}}

        Return JSON: { "services": [ {
          "service": string,
          "sku": string,
          "category": "Compute|AI|Data|Networking|Security|Observability",
          "meter": string,
          "assumption": string,
          "quantity": number,
          "nonProdQuantity": number,
          "unitPrice": number,
          "unit": string,
          "pricingReferenceUrl": string,
          "pricingReferenceLabel": string
        } ],
          "contingencyPercent": number
        }

        Always include: compute (App Service), storage (Blob), observability (Log Analytics), security
        (Key Vault). Include Foundry/Azure OpenAI token line items if the workload uses AI, and Azure AI
        Search if it uses document/file search. For pricingReferenceUrl, cite the official Microsoft Azure
        pricing details page for that service so each line item is auditable.

        DOCUMENTS:
        {{corpus}}
        """;

    private static CostEstimate CostFromPlan(ServicePlan plan, ScopeSummary scope)
    {
        var estimate = new CostEstimate
        {
            Currency = AzurePricingCatalog.Currency,
            Region = AzurePricingCatalog.Region,
            PricingBasis = "Foundry-proposed plan, priced with POC reference rates",
            ContingencyPercent = plan.ContingencyPercent is >= 10 and <= 40 ? plan.ContingencyPercent : 20m,
            Notes =
            {
                "Service plan proposed by Microsoft Foundry prompt agent; unit prices are reference estimates.",
                "Validate against the Azure Pricing Calculator / Retail Prices API before commitment.",
                "Each line item links to its first-party Azure pricing page for audit (shown in UI and Excel).",
                "Non-prod view models a scaled-down dev/test footprint of the same architecture; Total = Non-prod + Prod.",
                $"Scale assumption: {scope.ExpectedScale}"
            }
        };

        foreach (var service in plan.Services)
        {
            var quantity = service.Quantity;
            var unitPrice = service.UnitPrice;

            if (service.Service.Contains("App Service", StringComparison.OrdinalIgnoreCase) &&
                AzurePricingCatalog.AppServicePlanMonthly.TryGetValue(service.Sku, out var catalogPrice))
            {
                unitPrice = catalogPrice;
            }

            var nonProdQuantity = service.NonProdQuantity is { } npq && npq >= 0 && npq <= quantity
                ? Math.Round(npq, 4)
                : Math.Round(quantity * AzurePricingCatalog.NonProdFactor(service.Category), 4);

            var catalogRef = AzurePricingCatalog.ResolvePricingReference(service.Service);
            var referenceUrl = !string.IsNullOrWhiteSpace(service.PricingReferenceUrl) &&
                               service.PricingReferenceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? service.PricingReferenceUrl
                : catalogRef.Url;
            var referenceLabel = !string.IsNullOrWhiteSpace(service.PricingReferenceLabel)
                ? service.PricingReferenceLabel
                : catalogRef.Label;

            estimate.LineItems.Add(new CostLineItem
            {
                Service = service.Service,
                Sku = service.Sku,
                Meter = service.Meter,
                Assumption = service.Assumption,
                Quantity = quantity,
                UnitPrice = unitPrice,
                Unit = service.Unit,
                Category = string.IsNullOrWhiteSpace(service.Category) ? "Other" : service.Category,
                MonthlyCost = Math.Round(quantity * unitPrice, 2),
                NonProdQuantity = nonProdQuantity,
                PricingReferenceUrl = referenceUrl,
                PricingReferenceLabel = referenceLabel
            });
        }

        if (estimate.LineItems.Count == 0)
        {
            var reference = AzurePricingCatalog.ResolvePricingReference("Azure Blob Storage");
            estimate.Notes.Add("Foundry returned no service line items; supplementing with baseline storage/observability.");
            estimate.LineItems.Add(new CostLineItem
            {
                Service = "Azure Blob Storage",
                Sku = "Hot LRS",
                Meter = "GB-month",
                Assumption = "Baseline artefact storage",
                Quantity = 20,
                UnitPrice = AzurePricingCatalog.BlobHotPerGbMonth,
                Unit = "per GB/mo",
                Category = "Data",
                MonthlyCost = Math.Round(20 * AzurePricingCatalog.BlobHotPerGbMonth, 2),
                NonProdQuantity = Math.Round(20 * AzurePricingCatalog.NonProdFactor("Data"), 4),
                PricingReferenceUrl = reference.Url,
                PricingReferenceLabel = reference.Label
            });
        }

        return estimate;
    }

    private sealed class ServicePlan
    {
        [JsonPropertyName("services")]
        public List<ServicePlanItem> Services { get; set; } = new();

        [JsonPropertyName("contingencyPercent")]
        public decimal ContingencyPercent { get; set; } = 20m;
    }

    private sealed class ServicePlanItem
    {
        [JsonPropertyName("service")] public string Service { get; set; } = "";
        [JsonPropertyName("sku")] public string Sku { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("meter")] public string Meter { get; set; } = "";
        [JsonPropertyName("assumption")] public string Assumption { get; set; } = "";
        [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
        [JsonPropertyName("nonProdQuantity")] public decimal? NonProdQuantity { get; set; }
        [JsonPropertyName("unitPrice")] public decimal UnitPrice { get; set; }
        [JsonPropertyName("unit")] public string Unit { get; set; } = "";
        [JsonPropertyName("pricingReferenceUrl")] public string? PricingReferenceUrl { get; set; }
        [JsonPropertyName("pricingReferenceLabel")] public string? PricingReferenceLabel { get; set; }
    }
}
