using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services.Agents;

namespace Proj37.CostEstimator.Web.Services.Foundry;

/// <summary>
/// Estimation engine backed by dedicated Microsoft Foundry prompt agents for each pipeline step.
/// On any failure it resets partial state and falls back to the deterministic offline engine so the
/// existing end-to-end estimation API remains reliable.
/// </summary>
public sealed class FoundryEstimationEngine : IEstimationEngine
{
    private readonly FoundryOptions _options;
    private readonly OfflineEstimationEngine _offline;
    private readonly ScopeAgent _scopeAgent;
    private readonly RequirementsAgent _requirementsAgent;
    private readonly CostModelAgent _costModelAgent;
    private readonly ProjectCostAgent _projectCostAgent;
    private readonly OperationCostAgent _operationCostAgent;
    private readonly ILogger<FoundryEstimationEngine> _logger;

    public FoundryEstimationEngine(
        FoundryOptions options,
        OfflineEstimationEngine offline,
        ScopeAgent scopeAgent,
        RequirementsAgent requirementsAgent,
        CostModelAgent costModelAgent,
        ProjectCostAgent projectCostAgent,
        OperationCostAgent operationCostAgent,
        ILogger<FoundryEstimationEngine> logger)
    {
        _options = options;
        _offline = offline;
        _scopeAgent = scopeAgent;
        _requirementsAgent = requirementsAgent;
        _costModelAgent = costModelAgent;
        _projectCostAgent = projectCostAgent;
        _operationCostAgent = operationCostAgent;
        _logger = logger;
    }

    public string Name => "foundry";

    public async Task<EstimationResult> EstimateAsync(EstimationResult job, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogInformation("Foundry not configured; using offline engine.");
            await _offline.EstimateAsync(job, ct);
            job.AgentSteps.Insert(0, new AgentStepLog { Step = "engine", Summary = "Foundry disabled/unconfigured — used deterministic offline engine." });
            return job;
        }

        try
        {
            var corpus = BaseFoundryAgent.BuildCorpus(job.Documents);

            job.Scope = await _scopeAgent.RunAsync(corpus, ct) ?? throw new InvalidOperationException("Scope step returned no JSON.");
            job.AgentSteps.Add(new AgentStepLog { Step = "scope", Summary = $"Foundry agent summarised scope: {job.Scope.WorkloadProfile}." });

            job.Requirements = await _requirementsAgent.RunAsync(corpus, job.Scope, ct);
            job.AgentSteps.Add(new AgentStepLog { Step = "requirements", Summary = $"Foundry agent derived {job.Requirements.Count} requirements." });

            job.Cost = await _costModelAgent.RunAsync(corpus, job.Scope, ct);
            job.AgentSteps.Add(new AgentStepLog { Step = "cost", Summary = $"Foundry agent proposed {job.Cost.LineItems.Count} services; costed locally to {job.Cost.Currency} {job.Cost.MonthlyTotalWithContingency:N2}/mo (incl. contingency)." });

            job.ProjectCost = await _projectCostAgent.RunAsync(corpus, job.Scope, ct);
            job.AgentSteps.Add(new AgentStepLog { Step = "project", Summary = $"Foundry agent planned a {job.ProjectCost.Roles.Count}-role delivery team (~{job.ProjectCost.TotalDays:N0} person-days); build cost {job.ProjectCost.Currency} {job.ProjectCost.TotalWithContingency:N2} (incl. contingency)." });

            job.Operations = await _operationCostAgent.RunAsync(corpus, job.Scope, ct);
            job.AgentSteps.Add(new AgentStepLog { Step = "operations", Summary = $"Foundry agent estimated {job.Operations.Items.Count} operating line items; monthly run cost {job.Operations.Currency} {job.Operations.MonthlyTotalWithContingency:N2} (incl. contingency)." });

            job.Engine = Name;
            job.Status = "completed";
            return job;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Foundry estimation failed; falling back to offline engine.");
            job.Scope = new();
            job.Requirements = new();
            job.Cost = new();
            job.ProjectCost = new();
            job.Operations = new();
            job.AgentSteps.Clear();
            await _offline.EstimateAsync(job, ct);
            job.AgentSteps.Insert(0, new AgentStepLog { Step = "engine", Summary = $"Foundry call failed ({ex.GetType().Name}); fell back to offline engine. Detail: {BaseFoundryAgent.Trunc(ex.Message, 200)}" });
            return job;
        }
    }
}
