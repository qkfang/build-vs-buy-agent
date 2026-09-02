using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// A single cloud-neutral service catalog entry: a canonical <see cref="Key"/> (shared across providers,
/// e.g. "compute.appservice") mapped to the provider-specific service name, SKU, and first-party pricing
/// reference. Loaded from <c>Data/cloud-catalog/{provider}.json</c>.
///
/// These catalogs are intentionally plain, self-describing JSON files (one per provider) so they can be
/// read directly by other tooling/agents — for example exposed as an MCP resource/tool — without needing
/// this service or the rest of the app.
/// </summary>
public sealed class CloudCatalogEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("service")] public string Service { get; set; } = "";
    [JsonPropertyName("sku")] public string Sku { get; set; } = "";
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("pricingUrl")] public string PricingUrl { get; set; } = "";
    [JsonPropertyName("pricingLabel")] public string PricingLabel { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// <summary>Case-insensitive substrings used (Azure catalog only) to map a built cost line's service
    /// name back to this entry's canonical <see cref="Key"/> so it can be translated to another provider.</summary>
    [JsonPropertyName("matchKeywords")] public List<string> MatchKeywords { get; set; } = new();
}

/// <summary>
/// Loads the per-provider (Azure / GCP / AWS) service-catalog + pricing-reference JSON files bundled under
/// <c>Data/cloud-catalog/</c> and resolves the cloud-neutral equivalent of an Azure-costed service so the
/// Cost Model can be presented "as if built on" the project's selected cloud platform.
/// </summary>
public sealed class CloudCatalogService
{
    public static readonly IReadOnlyList<string> SupportedProviders = new[] { "azure", "gcp", "aws" };
    public const string DefaultProvider = "azure";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyDictionary<string, IReadOnlyList<CloudCatalogEntry>> _catalogs;

    public CloudCatalogService() : this(ResolveDefaultDir()) { }

    public CloudCatalogService(IWebHostEnvironment env)
        : this(Path.Combine(env.ContentRootPath, "Data", "cloud-catalog"))
    {
    }

    private CloudCatalogService(string dir)
    {
        var catalogs = new Dictionary<string, IReadOnlyList<CloudCatalogEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in SupportedProviders)
        {
            catalogs[provider] = LoadCatalog(Path.Combine(dir, provider + ".json"));
        }
        _catalogs = catalogs;
    }

    private static string ResolveDefaultDir() => Path.Combine(AppContext.BaseDirectory, "Data", "cloud-catalog");

    private static IReadOnlyList<CloudCatalogEntry> LoadCatalog(string path)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<CloudCatalogEntry>();
            var entries = JsonSerializer.Deserialize<List<CloudCatalogEntry>>(File.ReadAllText(path), JsonOpts);
            return entries ?? new List<CloudCatalogEntry>();
        }
        catch
        {
            return Array.Empty<CloudCatalogEntry>();
        }
    }

    /// <summary>Normalizes a requested provider id to one of <see cref="SupportedProviders"/>, defaulting to Azure.</summary>
    public static string NormalizeProvider(string? provider) =>
        !string.IsNullOrWhiteSpace(provider) && SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase)
            ? provider.Trim().ToLowerInvariant()
            : DefaultProvider;

    /// <summary>True when <paramref name="provider"/> is one of the supported cloud platform ids.</summary>
    public static bool IsSupported(string? provider) =>
        !string.IsNullOrWhiteSpace(provider) && SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);

    /// <summary>The full service catalog for a provider (empty list if the provider is unknown or the file failed to load).</summary>
    public IReadOnlyList<CloudCatalogEntry> GetCatalog(string provider) =>
        _catalogs.TryGetValue(NormalizeProvider(provider), out var list) ? list : Array.Empty<CloudCatalogEntry>();

    /// <summary>
    /// Resolves the <paramref name="targetProvider"/> equivalent of an Azure-costed line item's service name.
    /// Returns null when the target provider is Azure itself, or no catalog match is found.
    /// </summary>
    public CloudCatalogEntry? Resolve(string targetProvider, string? azureServiceName)
    {
        var provider = NormalizeProvider(targetProvider);
        if (provider == DefaultProvider || string.IsNullOrWhiteSpace(azureServiceName))
            return null;

        var azureCatalog = GetCatalog(DefaultProvider);
        var match = azureCatalog.FirstOrDefault(e =>
            e.MatchKeywords.Any(k => azureServiceName.Contains(k, StringComparison.OrdinalIgnoreCase)));
        var key = match?.Key ?? "generic.other";

        var targetCatalog = GetCatalog(provider);
        return targetCatalog.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Renames each line item's service/SKU and pricing reference to <paramref name="targetProvider"/>'s catalog
    /// equivalent (no-op when the target is Azure). Quantities and reference unit prices are left untouched —
    /// the Azure reference rates remain the POC's cost basis; only the target platform's service naming and
    /// first-party pricing link are surfaced. Appends an explanatory note to <paramref name="estimate"/>.
    /// </summary>
    public void ApplyToEstimate(Models.CostEstimate estimate, string targetProvider)
    {
        var provider = NormalizeProvider(targetProvider);
        if (provider == DefaultProvider) return;

        foreach (var item in estimate.LineItems)
        {
            var entry = Resolve(provider, item.Service);
            if (entry is null) continue;

            item.Service = entry.Service;
            if (!string.IsNullOrWhiteSpace(entry.Sku)) item.Sku = entry.Sku;
            item.PricingReferenceUrl = entry.PricingUrl;
            item.PricingReferenceLabel = entry.PricingLabel;
        }

        estimate.Notes.Add(
            $"Services and pricing references translated to the {provider.ToUpperInvariant()} equivalent catalog " +
            "(Data/cloud-catalog); reference unit prices remain the Azure POC rates as a cross-cloud approximation.");
    }
}
