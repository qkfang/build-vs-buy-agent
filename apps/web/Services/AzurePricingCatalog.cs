using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Curated reference pricing for common Azure services used in POC estimation.
///
/// IMPORTANT: These are *reference list prices* captured for a stable POC experience and are NOT a
/// live pricing feed. Region: Australia East, currency USD, Pay-As-You-Go. For production estimates,
/// replace this with the Azure Retail Prices API (https://prices.azure.com/api/retail/prices) or the
/// Azure Pricing Calculator export. Rates are intentionally rounded and conservative.
///
/// Rationale for hard-coded reference rates is documented in docs/solution.md and called out in the
/// generated Excel workbook so consumers never mistake POC numbers for a binding quote.
/// </summary>
public static class AzurePricingCatalog
{
    public const string Region = "australiaeast";
    public const string Currency = "USD";

    /// <summary>App Service plan monthly price per instance (Linux, 730 hrs).</summary>
    public static readonly IReadOnlyDictionary<string, decimal> AppServicePlanMonthly = new Dictionary<string, decimal>
    {
        ["B1"] = 13.14m,
        ["B2"] = 26.28m,
        ["S1"] = 73.00m,
        ["P0v3"] = 64.97m,
        ["P1v3"] = 129.94m,
        ["P2v3"] = 259.88m,
    };

    /// <summary>Azure OpenAI / Foundry model token prices (USD per 1K tokens).</summary>
    public sealed record ModelTokenRate(decimal InputPer1K, decimal OutputPer1K);

    public static readonly IReadOnlyDictionary<string, ModelTokenRate> ModelTokenRates = new Dictionary<string, ModelTokenRate>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o"] = new(0.0050m, 0.0150m),
        ["gpt-4o-mini"] = new(0.000165m, 0.00066m),
        ["gpt-5.4"] = new(0.0050m, 0.0150m),   // reference; treated like a flagship GPT tier
    };

    public static ModelTokenRate GetModelRate(string deployment)
        => ModelTokenRates.TryGetValue(deployment, out var r) ? r : ModelTokenRates["gpt-4o"];

    // --- Flat per-unit reference rates (USD) ---

    /// <summary>Azure AI Search Basic tier, per month.</summary>
    public const decimal AiSearchBasicMonthly = 75.00m;

    /// <summary>Azure AI Search Standard S1, per month.</summary>
    public const decimal AiSearchStandardMonthly = 250.00m;

    /// <summary>Blob storage (Hot LRS) per GB-month.</summary>
    public const decimal BlobHotPerGbMonth = 0.021m;

    /// <summary>Azure SQL Database, General Purpose serverless, per vCore-month (approx if always-on).</summary>
    public const decimal SqlGpServerlessVCoreMonth = 130.00m;

    /// <summary>Azure SQL Database Basic (5 DTU), per month.</summary>
    public const decimal SqlBasicMonthly = 4.90m;

    /// <summary>Cosmos DB serverless, per million RUs.</summary>
    public const decimal CosmosServerlessPerMillionRu = 0.28m;

    /// <summary>Log Analytics ingestion per GB.</summary>
    public const decimal LogAnalyticsPerGb = 2.76m;

    /// <summary>Application Insights is billed via Log Analytics ingestion (same meter).</summary>

    /// <summary>Key Vault operations, per 10K operations.</summary>
    public const decimal KeyVaultPer10KOps = 0.03m;

    /// <summary>Standard public IP / outbound data transfer, per GB (first tier).</summary>
    public const decimal EgressPerGb = 0.12m;

    /// <summary>Azure Functions consumption — per million executions (after free grant).</summary>
    public const decimal FunctionsPerMillionExec = 0.20m;

    // ---------------------------------------------------------------- Pricing reference links
    //
    // Each cost line surfaces a first-party Azure pricing reference so reviewers can audit the rate
    // against Microsoft's published pricing. URLs are the canonical azure.microsoft.com/pricing/details
    // pages that the Microsoft Learn cost-management docs themselves link to (verified via Microsoft
    // Learn). They are deliberately service-level (not deep meter anchors) so they stay stable.

    public sealed record PricingRef(string Label, string Url);

    /// <summary>Generic fallback: the Azure Pricing Calculator + Retail Prices API.</summary>
    public static readonly PricingRef PricingCalculator =
        new("Azure Pricing Calculator", "https://azure.microsoft.com/pricing/calculator/");

    /// <summary>
    /// Service-name (substring, case-insensitive) -> first-party pricing reference. Ordered most-specific
    /// first; <see cref="ResolvePricingReference"/> returns the first contains-match.
    /// </summary>
    private static readonly (string Match, PricingRef Ref)[] PricingReferences =
    {
        ("App Service",     new("App Service pricing",        "https://azure.microsoft.com/pricing/details/app-service/linux/")),
        ("AI Search",       new("Azure AI Search pricing",     "https://azure.microsoft.com/pricing/details/search/")),
        ("Cognitive Search",new("Azure AI Search pricing",     "https://azure.microsoft.com/pricing/details/search/")),
        ("Foundry",         new("Azure OpenAI pricing",        "https://azure.microsoft.com/pricing/details/cognitive-services/openai-service/")),
        ("OpenAI",          new("Azure OpenAI pricing",        "https://azure.microsoft.com/pricing/details/cognitive-services/openai-service/")),
        ("SQL",             new("Azure SQL Database pricing",  "https://azure.microsoft.com/pricing/details/azure-sql-database/single/")),
        ("Cosmos",          new("Azure Cosmos DB pricing",     "https://azure.microsoft.com/pricing/details/cosmos-db/autoscale-provisioned/")),
        ("Functions",       new("Azure Functions pricing",     "https://azure.microsoft.com/pricing/details/functions/")),
        ("Blob",            new("Blob Storage pricing",        "https://azure.microsoft.com/pricing/details/storage/blobs/")),
        ("Storage",         new("Azure Storage pricing",       "https://azure.microsoft.com/pricing/details/storage/blobs/")),
        ("Log Analytics",   new("Azure Monitor pricing",       "https://azure.microsoft.com/pricing/details/monitor/")),
        ("App Insights",    new("Azure Monitor pricing",       "https://azure.microsoft.com/pricing/details/monitor/")),
        ("Application Insights", new("Azure Monitor pricing",  "https://azure.microsoft.com/pricing/details/monitor/")),
        ("Monitor",         new("Azure Monitor pricing",       "https://azure.microsoft.com/pricing/details/monitor/")),
        ("Key Vault",       new("Key Vault pricing",           "https://azure.microsoft.com/pricing/details/key-vault/")),
        ("Bandwidth",       new("Bandwidth pricing",           "https://azure.microsoft.com/pricing/details/bandwidth/")),
        ("Egress",          new("Bandwidth pricing",           "https://azure.microsoft.com/pricing/details/bandwidth/")),
    };

    /// <summary>Best-effort first-party pricing reference for a service name; never null.</summary>
    public static PricingRef ResolvePricingReference(string? service)
    {
        if (!string.IsNullOrWhiteSpace(service))
        {
            foreach (var (match, reference) in PricingReferences)
            {
                if (service.Contains(match, StringComparison.OrdinalIgnoreCase))
                    return reference;
            }
        }
        return PricingCalculator;
    }

    // ---------------------------------------------------------------- Non-prod sizing factors
    //
    // Non-production (dev/test/POC) runs the SAME architecture at a scaled-down footprint. We model that
    // per category as a fraction of the production monthly quantity. Aligns with Microsoft's guidance that
    // non-prod typically uses lower tiers / fewer instances (see App Service "Nonproduction workloads").
    private static readonly IReadOnlyDictionary<string, decimal> NonProdFactorByCategory =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["Compute"] = 0.50m,        // fewer / smaller instances
            ["AI"] = 0.30m,             // far lower request volume in dev/test
            ["Data"] = 0.40m,           // smaller DBs / less storage
            ["Networking"] = 0.30m,     // little egress in non-prod
            ["Security"] = 0.50m,       // similar baseline, slightly lower op volume
            ["Observability"] = 0.40m,  // lower telemetry volume
        };

    public const decimal DefaultNonProdFactor = 0.40m;

    /// <summary>Non-prod quantity multiplier for a cost category (0..1).</summary>
    public static decimal NonProdFactor(string? category)
        => category is not null && NonProdFactorByCategory.TryGetValue(category, out var f) ? f : DefaultNonProdFactor;
}
