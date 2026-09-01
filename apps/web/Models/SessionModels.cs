namespace Proj37.CostEstimator.Web.Models;

/// <summary>
/// A persisted multi-step agent session. Unlike the legacy estimation job, a session is created from
/// uploaded documents first and each agent-backed step is then run or re-run independently.
/// </summary>
public sealed class AgentSession
{
    public static readonly IReadOnlyList<string> StepOrder =
        new[] { "scope", "requirements", "cost", "project", "operations", "compare" };

    public string SessionId { get; set; } = $"session-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<IngestedDocument> Documents { get; set; } = new();
    public string Engine { get; set; } = "offline";
    public ScopeSummary? Scope { get; set; }
    public List<TechnicalRequirement> Requirements { get; set; } = new();
    public CostEstimate? Cost { get; set; }
    public ProjectBuildCost? ProjectCost { get; set; }
    public OperationCost? Operations { get; set; }
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
