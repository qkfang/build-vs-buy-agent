using System.Collections.Concurrent;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using Microsoft.Agents.AI;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services.Foundry;

/// <summary>
/// Creates (or reuses) persistent Microsoft Foundry prompt agents — one per pipeline step — inside the
/// configured Foundry project, and hands back live <see cref="AIAgent"/> handles bound to those
/// server-side agent versions.
/// </summary>
/// <remarks>
/// This replaces the previous <c>AsAIAgent(model, instructions)</c> pattern, which only built a local
/// agent object and therefore never showed up as an agent in the Foundry project. Here every step is
/// registered through <see cref="AgentAdministrationClient"/> so the agents exist in Foundry, are
/// versioned, and every run is attributable to a named agent.
/// </remarks>
public sealed class FoundryAgentProvisioner
{
    private readonly FoundryOptions _options;
    private readonly ILogger<FoundryAgentProvisioner> _logger;
    private readonly Lazy<AIProjectClient> _projectClient;
    private readonly Lazy<AgentAdministrationClient> _adminClient;
    private readonly ConcurrentDictionary<string, Task<AIAgent>> _agents = new(StringComparer.OrdinalIgnoreCase);

    public FoundryAgentProvisioner(FoundryOptions options, ILogger<FoundryAgentProvisioner> logger)
    {
        _options = options;
        _logger = logger;

        _projectClient = new Lazy<AIProjectClient>(
            () => new AIProjectClient(new Uri(RequireEndpoint()), CreateCredential()),
            LazyThreadSafetyMode.ExecutionAndPublication);

        _adminClient = new Lazy<AgentAdministrationClient>(
            () => new AgentAdministrationClient(new Uri(RequireEndpoint()), CreateCredential()),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Names of the agents provisioned so far, for diagnostics/health reporting.</summary>
    public IReadOnlyCollection<string> ProvisionedAgentNames => _agents.Keys.ToArray();

    /// <summary>
    /// Returns an <see cref="AIAgent"/> bound to the persistent Foundry agent named
    /// <paramref name="agentName"/>, creating the agent (or a new version of it) when the desired
    /// model/instructions do not match what is already published.
    /// </summary>
    public Task<AIAgent> GetAgentAsync(string agentName, string instructions, CancellationToken ct = default)
    {
        // Provisioning is cached per agent name for the lifetime of the process. A caller-supplied token
        // is deliberately NOT captured: it would let one cancelled request poison the shared cache entry.
        return _agents.GetOrAdd(agentName, name => ProvisionGuardedAsync(name, instructions));
    }

    private async Task<AIAgent> ProvisionGuardedAsync(string agentName, string instructions)
    {
        try
        {
            return await ProvisionAsync(agentName, instructions, CancellationToken.None);
        }
        catch
        {
            _agents.TryRemove(agentName, out _);
            throw;
        }
    }

    private async Task<AIAgent> ProvisionAsync(string agentName, string instructions, CancellationToken ct)
    {
        var admin = _adminClient.Value;
        var version = await FindReusableVersionAsync(admin, agentName, instructions, ct);

        if (version is null)
        {
            var definition = new DeclarativeAgentDefinition(_options.ModelDeploymentName)
            {
                Instructions = instructions
            };

            var creation = new ProjectsAgentVersionCreationOptions(definition)
            {
                Description = $"proj37 build-vs-buy estimator — {agentName}"
            };

            version = await admin.CreateAgentVersionAsync(agentName, creation, cancellationToken: ct);
            _logger.LogInformation(
                "Created Foundry agent {AgentName} version {AgentVersion} (model {Model}).",
                agentName, version.Version, _options.ModelDeploymentName);
        }
        else
        {
            _logger.LogInformation(
                "Reusing existing Foundry agent {AgentName} version {AgentVersion}.",
                agentName, version.Version);
        }

        return _projectClient.Value.AsAIAgent(version);
    }

    /// <summary>
    /// Returns the most recent published version of the agent when its model and instructions already
    /// match what this build wants, so restarts do not pile up identical versions in the project.
    /// </summary>
    private async Task<ProjectsAgentVersion?> FindReusableVersionAsync(
        AgentAdministrationClient admin,
        string agentName,
        string instructions,
        CancellationToken ct)
    {
        try
        {
            await foreach (var candidate in admin.GetAgentVersionsAsync(
                agentName, limit: 1, order: AgentListOrder.Descending, cancellationToken: ct))
            {
                if (candidate.Definition is DeclarativeAgentDefinition declarative
                    && string.Equals(declarative.Model, _options.ModelDeploymentName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(declarative.Instructions, instructions, StringComparison.Ordinal))
                {
                    return candidate;
                }

                return null;
            }
        }
        catch (Exception ex)
        {
            // A missing agent is the normal first-run path; anything else is still recoverable by creating.
            _logger.LogDebug(ex, "No reusable Foundry agent version found for {AgentName}.", agentName);
        }

        return null;
    }

    private string RequireEndpoint() =>
        _options.ProjectEndpoint
        ?? throw new InvalidOperationException("Foundry:ProjectEndpoint is not configured.");

    private DefaultAzureCredential CreateCredential()
    {
        var credOptions = new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeInteractiveBrowserCredential = true,
        };

        if (!string.IsNullOrWhiteSpace(_options.TenantId))
        {
            credOptions.TenantId = _options.TenantId;
        }

        return new DefaultAzureCredential(credOptions);
    }
}
