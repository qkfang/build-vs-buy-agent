using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services.Foundry;

namespace Proj37.CostEstimator.Web.Services.Agents;

/// <summary>
/// Foundry agent for the Buy tab's Spec step. Reads the vendor/off-the-shelf documents uploaded on this
/// step (in addition to the original session documents) and produces a structured spec summary that the
/// Purchase and Operation Cost (Buy) steps depend on.
/// </summary>
public sealed class SpecAgent : BaseFoundryAgent
{
    public SpecAgent(FoundryOptions options, FoundryAgentProvisioner provisioner, ILogger<SpecAgent> logger)
        : base(options, provisioner, logger, AgentInstructions.Spec)
    {
    }

    protected override string AgentNameSuffix => "spec-agent";

    public async Task<BuySpecSummary> RunAsync(string buyCorpus, ScopeSummary? scope, CancellationToken ct)
    {
        var agent = await GetAgentAsync(ct);
        var spec = await RunJsonAsync<BuySpecSummary>(agent, SpecPrompt(buyCorpus, scope), ct);
        if (spec is null)
            throw new InvalidOperationException("Spec step returned no JSON.");

        NormalizeSpec(spec);
        return spec;
    }

    private string SpecPrompt(string buyCorpus, ScopeSummary? scope) =>
        $$"""
        {{StepInstruction.Instructions}}

        Analyze the following off-the-shelf/vendor document(s) and produce a SPEC summary of the
        product being considered for Buy.

        SCOPE (for context on what capability is being replaced): {{Serialize(scope ?? new ScopeSummary())}}

        Return JSON with exactly these fields:
        {
          "vendorName": string,
          "productOverview": string,
          "keyCapabilities": string[],
          "constraints": string[],
          "licensingModel": string
        }

        BUY DOCUMENTS:
        {{buyCorpus}}
        """;

    private static void NormalizeSpec(BuySpecSummary spec)
    {
        spec.VendorName = string.IsNullOrWhiteSpace(spec.VendorName) ? "Unidentified vendor" : spec.VendorName;
        spec.ProductOverview = string.IsNullOrWhiteSpace(spec.ProductOverview)
            ? "No product overview could be derived from the uploaded documents."
            : spec.ProductOverview;
    }
}
