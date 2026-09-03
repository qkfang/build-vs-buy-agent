namespace Proj37.CostEstimator.Web.Models;

/// <summary>
/// A persisted multi-step agent session. Unlike the legacy estimation job, a session is created from
/// uploaded documents first and each agent-backed step is then run or re-run independently.
/// </summary>
public sealed class AgentSession
{
    public static readonly IReadOnlyList<string> StepOrder =
        new[] { "scope", "requirements", "features", "cost", "project", "operations", "spec", "purchase", "buyoperations", "compare" };

    public string SessionId { get; set; } = $"session-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<IngestedDocument> Documents { get; set; } = new();

    /// <summary>
    /// Documents uploaded specifically for the Buy tab's Spec step — the vendor / off-the-shelf
    /// solution's spec and cost material, additional to the original session <see cref="Documents"/>.
    /// </summary>
    public List<IngestedDocument> BuyDocuments { get; set; } = new();
    public string Engine { get; set; } = "offline";

    /// <summary>
    /// The target cloud platform ("azure" | "gcp" | "aws") this session's services should be built on.
    /// Persisted with the session and used to translate the Cost Model's service catalog + pricing
    /// references (see <c>Data/cloud-catalog/{provider}.json</c> and <see cref="Services.CloudCatalogService"/>).
    /// </summary>
    public string CloudProvider { get; set; } = Services.CloudCatalogService.DefaultProvider;
    public ScopeSummary? Scope { get; set; }
    public List<TechnicalRequirement> Requirements { get; set; } = new();
    public FeatureSet? Features { get; set; }
    public CostEstimate? Cost { get; set; }
    public ProjectBuildCost? ProjectCost { get; set; }
    public OperationCost? Operations { get; set; }
    public BuySpecSummary? Spec { get; set; }
    public PurchaseCost? Purchase { get; set; }
    public OperationCost? BuyOperations { get; set; }
    public CostComparison? Compare { get; set; }
    public List<AgentStepLog> AgentSteps { get; set; } = new();
    public Dictionary<string, StepState> Steps { get; set; } = CreateDefaultSteps();

    public static Dictionary<string, StepState> CreateDefaultSteps() =>
        StepOrder.ToDictionary(step => step, _ => new StepState());
}

/// <summary>
/// The persisted execution state for one step inside an <see cref="AgentSession"/>.
/// </summary>
public sealed class StepState
{
    public string Status { get; set; } = "pending";
    public DateTimeOffset? LastRunUtc { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Lightweight list item returned by the session-history endpoint.
/// </summary>
public sealed class AgentSessionSummary
{
    public string SessionId { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? Project { get; set; }
    public int Documents { get; set; }
    public string Status { get; set; } = "pending";
}
