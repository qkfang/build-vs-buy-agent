using Proj37.CostEstimator.Web.Services.Agents;

namespace Proj37.CostEstimator.Web.Services.Foundry;

/// <summary>
/// Provisions every step's persistent Foundry agent once at startup. Failures are logged and
/// swallowed: the estimation pipeline still falls back to the deterministic offline engine, and each
/// agent retries provisioning on first use.
/// </summary>
public sealed class FoundryAgentBootstrapper : IHostedService
{
    private readonly IEnumerable<BaseFoundryAgent> _agents;
    private readonly ILogger<FoundryAgentBootstrapper> _logger;

    public FoundryAgentBootstrapper(IServiceProvider services, ILogger<FoundryAgentBootstrapper> logger)
    {
        _agents = new BaseFoundryAgent[]
        {
            services.GetRequiredService<ScopeAgent>(),
            services.GetRequiredService<RequirementsAgent>(),
            services.GetRequiredService<FeaturesAgent>(),
            services.GetRequiredService<CostModelAgent>(),
            services.GetRequiredService<ProjectCostAgent>(),
            services.GetRequiredService<OperationCostAgent>(),
            services.GetRequiredService<SpecAgent>(),
            services.GetRequiredService<PurchaseAgent>(),
            services.GetRequiredService<BuyOperationCostAgent>(),
            services.GetRequiredService<CompareAgent>(),
        };
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents)
        {
            try
            {
                await agent.EnsureAgentAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not provision Foundry agent {AgentName} at startup.", agent.FoundryAgentName);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
