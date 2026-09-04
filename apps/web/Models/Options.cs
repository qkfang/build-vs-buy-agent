namespace Proj37.CostEstimator.Web.Models;

/// <summary>
/// Bound from configuration section "Foundry". Controls how the estimation pipeline talks to
/// Microsoft Foundry. When the endpoint is missing, the app uses the deterministic offline
/// estimation engine so the POC is fully runnable without live Azure access.
/// </summary>
public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    /// <summary>Foundry project endpoint, e.g. https://&lt;name&gt;.services.ai.azure.com/api/projects/&lt;project&gt;.</summary>
    public string? ProjectEndpoint { get; set; }

    /// <summary>Optional Entra tenant ID to authenticate against (the tenant that owns the Foundry project).</summary>
    public string? TenantId { get; set; }

    /// <summary>Model deployment name, e.g. gpt-5.5.</summary>
    public string ModelDeploymentName { get; set; } = "gpt-5.5";

    /// <summary>Prefix for the persistent Foundry agent names, e.g. "bvb" -&gt; <c>bvb-scope-agent</c>.</summary>
    public string AgentName { get; set; } = "bvb";

    /// <summary>When true, attach uploaded docs to a Foundry vector store and enable file search.</summary>
    public bool UseFileSearch { get; set; } = true;

    /// <summary>True when configuration is sufficient to attempt a live run.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectEndpoint);
}

/// <summary>Bound from "Storage". Optional blob persistence of uploads / generated workbooks.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string? AccountUrl { get; set; }
    public string ContainerName { get; set; } = "estimations";

    /// <summary>Local folder for job persistence (App Service: /home/site/data).</summary>
    public string LocalDataFolder { get; set; } = "App_Data";

    public bool UseBlob => !string.IsNullOrWhiteSpace(AccountUrl);
}
