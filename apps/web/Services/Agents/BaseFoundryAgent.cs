using System.Text.Json;
using Microsoft.Agents.AI;
using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services.Foundry;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Shared base class for the per-step Microsoft Foundry agents. It centralizes agent resolution,
/// JSON extraction/deserialization, and bounded document-corpus construction so each concrete step
/// class can stay focused on its own prompt and output normalization.
/// </summary>
public abstract class BaseFoundryAgent
{
    protected static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly FoundryAgentProvisioner _provisioner;

    protected BaseFoundryAgent(
        FoundryOptions options,
        FoundryAgentProvisioner provisioner,
        ILogger logger,
        AgentInstructions.StepInstruction stepInstruction)
    {
        Options = options;
        _provisioner = provisioner;
        Logger = logger;
        StepInstruction = stepInstruction;
        StepKey = stepInstruction.Key;
        AgentDisplayName = stepInstruction.Agent;
    }

    protected FoundryOptions Options { get; }
    protected ILogger Logger { get; }
    protected AgentInstructions.StepInstruction StepInstruction { get; }

    public string StepKey { get; }
    public string AgentDisplayName { get; }

    /// <summary>
    /// Suffix appended to <see cref="FoundryOptions.AgentName"/> when registering the concrete runtime
    /// agent with Foundry.
    /// </summary>
    protected virtual string AgentNameSuffix => $"{StepKey}-agent";

    /// <summary>
    /// System-level instructions published on the persistent Foundry agent for this step.
    /// Concrete steps can override this when they need a different base instruction block.
    /// </summary>
    protected virtual string AgentInstructionsText =>
        $"{AgentInstructions.SystemPersona}\n\n{StepInstruction.Instructions}";

    /// <summary>Resolves the persistent Foundry agent for this step, creating it on first use.</summary>
    protected Task<AIAgent> GetAgentAsync(CancellationToken ct) =>
        _provisioner.GetAgentAsync(ResolveAgentName(), AgentInstructionsText, ct);

    /// <summary>Full name of the persistent Foundry agent backing this step.</summary>
    public string FoundryAgentName => ResolveAgentName();

    protected async Task<T?> RunJsonAsync<T>(AIAgent agent, string prompt, CancellationToken ct)
    {
        var response = await agent.RunAsync(prompt, cancellationToken: ct);
        var text = response.Text ?? string.Empty;
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
            return default;

        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    public static string BuildCorpus(IEnumerable<IngestedDocument> documents)
    {
        var docs = string.Join(
            "\n\n",
            documents.Select(d => $"=== FILE: {d.FileName} ({d.WordCount} words) ===\n{d.ExtractedText}"));

        return Trunc(docs, 48_000);
    }

    public static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    protected static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOpts);

    private string ResolveAgentName() =>
        string.IsNullOrWhiteSpace(Options.AgentName)
            ? AgentNameSuffix
            : $"{Options.AgentName}-{AgentNameSuffix}";

    /// <summary>Provisions this step's Foundry agent without running it (startup warm-up).</summary>
    public Task EnsureAgentAsync(CancellationToken ct) => GetAgentAsync(ct);

    private static string? ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text.Substring(start, end - start + 1);
    }
}
