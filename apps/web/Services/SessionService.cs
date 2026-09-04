using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services.Agents;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Persists upload-first, run-steps-later agent sessions under the configured local data folder.
/// Each session lives in its own <c>session-*</c> directory with a <c>session.json</c> file and,
/// once enough steps have completed, a generated Excel workbook.
/// </summary>
public sealed partial class SessionService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [GeneratedRegex(@"^session-\d{17}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    private readonly DocumentIngestionService _ingestion;
    private readonly OfflineEstimationEngine _offline;
    private readonly ScopeAgent _scopeAgent;
    private readonly RequirementsAgent _requirementsAgent;
    private readonly FeaturesAgent _featuresAgent;
    private readonly CostModelAgent _costModelAgent;
    private readonly ProjectCostAgent _projectCostAgent;
    private readonly OperationCostAgent _operationCostAgent;
    private readonly SpecAgent _specAgent;
    private readonly PurchaseAgent _purchaseAgent;
    private readonly BuyOperationCostAgent _buyOperationCostAgent;
    private readonly CostComparisonService _comparisonService;
    private readonly ExcelReportGenerator _excel;
    private readonly FoundryOptions _foundryOptions;
    private readonly ILogger<SessionService> _logger;
    private readonly string _dataDir;
    private readonly object _ioLock = new();
    private readonly ConcurrentDictionary<string, AgentSession> _cache = new();

    public SessionService(
        DocumentIngestionService ingestion,
        OfflineEstimationEngine offline,
        ScopeAgent scopeAgent,
        RequirementsAgent requirementsAgent,
        FeaturesAgent featuresAgent,
        CostModelAgent costModelAgent,
        ProjectCostAgent projectCostAgent,
        OperationCostAgent operationCostAgent,
        SpecAgent specAgent,
        PurchaseAgent purchaseAgent,
        BuyOperationCostAgent buyOperationCostAgent,
        CostComparisonService comparisonService,
        ExcelReportGenerator excel,
        FoundryOptions foundryOptions,
        StorageOptions storage,
        IWebHostEnvironment env,
        ILogger<SessionService> logger)
    {
        _ingestion = ingestion;
        _offline = offline;
        _scopeAgent = scopeAgent;
        _requirementsAgent = requirementsAgent;
        _featuresAgent = featuresAgent;
        _costModelAgent = costModelAgent;
        _projectCostAgent = projectCostAgent;
        _operationCostAgent = operationCostAgent;
        _specAgent = specAgent;
        _purchaseAgent = purchaseAgent;
        _buyOperationCostAgent = buyOperationCostAgent;
        _comparisonService = comparisonService;
        _excel = excel;
        _foundryOptions = foundryOptions;
        _logger = logger;

        _dataDir = Path.IsPathRooted(storage.LocalDataFolder)
            ? storage.LocalDataFolder
            : Path.Combine(env.ContentRootPath, storage.LocalDataFolder);
        Directory.CreateDirectory(_dataDir);
        LoadExisting();
    }

    /// <summary>
    /// Creates a new persisted session from uploaded files without running any agent step yet.
    /// </summary>
    public async Task<AgentSession> CreateSessionAsync(IEnumerable<EstimationJobService.UploadedFile> files, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AgentSession
        {
            SessionId = await CreateSessionIdAsync(ct),
            CreatedUtc = now,
            UpdatedUtc = now,
            Engine = _foundryOptions.IsConfigured ? "foundry" : "offline",
            Steps = AgentSession.CreateDefaultSteps()
        };

        foreach (var file in files)
        {
            if (!_ingestion.IsSupported(file.FileName))
            {
                _logger.LogWarning("Skipping unsupported session upload file {File}", file.FileName);
                continue;
            }

            var document = await _ingestion.IngestAsync(file.FileName, file.ContentType, file.Content, ct);
            session.Documents.Add(document);
        }

        if (session.Documents.Count == 0)
        {
            throw new InvalidOperationException("No supported documents were provided. Supported: " + string.Join(", ", _ingestion.SupportedExtensions));
        }

        Persist(session);
        return session;
    }

    /// <summary>
    /// Adds one or more uploaded "Buy" documents (vendor spec / cost sheets) to the session, distinct from
    /// the original session documents used for the Build tab.
    /// </summary>
    public async Task<AgentSession> AddBuyDocumentsAsync(string sessionId, IEnumerable<EstimationJobService.UploadedFile> files, CancellationToken ct = default)
    {
        var session = Get(sessionId) ?? throw new FileNotFoundException("Session not found.", sessionId);

        var added = 0;
        foreach (var file in files)
        {
            if (!_ingestion.IsSupported(file.FileName))
            {
                _logger.LogWarning("Skipping unsupported Buy document upload {File}", file.FileName);
                continue;
            }

            var document = await _ingestion.IngestAsync(file.FileName, file.ContentType, file.Content, ct);
            session.BuyDocuments.Add(document);
            added++;
        }

        if (added == 0)
        {
            throw new InvalidOperationException("No supported documents were provided. Supported: " + string.Join(", ", _ingestion.SupportedExtensions));
        }

        session.UpdatedUtc = DateTimeOffset.UtcNow;
        Persist(session);
        return session;
    }

    /// <summary>
    /// Lists persisted sessions newest first.
    /// </summary>
    public IReadOnlyList<AgentSessionSummary> List() =>
        _cache.Values
            .OrderByDescending(s => s.CreatedUtc)
            .Select(ToSummary)
            .ToList();

    /// <summary>
    /// Gets the full session JSON by id.
    /// </summary>
    public AgentSession? Get(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
            return null;

        if (_cache.TryGetValue(sessionId, out var cached))
        {
            EnsureStepDictionary(cached);
            return cached;
        }

        var path = SessionJsonPath(sessionId);
        if (!File.Exists(path))
            return null;

        try
        {
            var session = JsonSerializer.Deserialize<AgentSession>(File.ReadAllText(path), JsonOpts);
            if (session is null)
                return null;

            EnsureStepDictionary(session);
            _cache[sessionId] = session;
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// Sets the target cloud platform ("azure" | "gcp" | "aws") for a session and persists the selection.
    /// Does not re-run any step; re-run the Cost Model step afterwards to reprice/rename services for the
    /// newly selected provider.
    /// </summary>
    public AgentSession? SetCloudProvider(string sessionId, string provider)
    {
        if (!CloudCatalogService.IsSupported(provider))
            throw new ArgumentException($"Unsupported cloud provider '{provider}'. Expected one of: {string.Join(", ", CloudCatalogService.SupportedProviders)}.", nameof(provider));

        var session = Get(sessionId);
        if (session is null)
            return null;

        session.CloudProvider = CloudCatalogService.NormalizeProvider(provider);
        session.UpdatedUtc = DateTimeOffset.UtcNow;
        Persist(session);
        return session;
    }

    /// <summary>
    /// Runs or re-runs one session step and persists the updated session state.
    /// </summary>
    public async Task<AgentSession> RunStepAsync(string sessionId, string step, CancellationToken ct = default)
    {
        if (!AgentSession.StepOrder.Contains(step, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown step '{step}'.", nameof(step));

        var session = Get(sessionId) ?? throw new FileNotFoundException("Session not found.", sessionId);
        EnsureStepDictionary(session);
        step = step.ToLowerInvariant();
        EnsurePrerequisites(session, step);

        var state = session.Steps[step];
        state.Status = "running";
        state.LastRunUtc = DateTimeOffset.UtcNow;
        state.Error = null;
        session.UpdatedUtc = DateTimeOffset.UtcNow;
        Persist(session);

        try
        {
            switch (step)
            {
                case "scope":
                    await RunScopeAsync(session, ct);
                    break;
                case "requirements":
                    await RunRequirementsAsync(session, ct);
                    break;
                case "features":
                    await RunFeaturesAsync(session, ct);
                    break;
                case "cost":
                    await RunCostAsync(session, ct);
                    break;
                case "project":
                    await RunProjectAsync(session, ct);
                    break;
                case "operations":
                    await RunOperationsAsync(session, ct);
                    break;
                case "spec":
                    await RunSpecAsync(session, ct);
                    break;
                case "purchase":
                    await RunPurchaseAsync(session, ct);
                    break;
                case "buyoperations":
                    await RunBuyOperationsAsync(session, ct);
                    break;
                case "compare":
                    await RunCompareAsync(session, ct);
                    break;
            }

            state.Status = "completed";
            state.LastRunUtc = DateTimeOffset.UtcNow;
            state.Error = null;
            session.UpdatedUtc = DateTimeOffset.UtcNow;
            EnsureWorkbookPersisted(session);
            Persist(session);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session step {Step} failed for {SessionId}", step, sessionId);
            state.Status = "failed";
            state.LastRunUtc = DateTimeOffset.UtcNow;
            state.Error = ex.Message;
            session.UpdatedUtc = DateTimeOffset.UtcNow;
            Persist(session);
            return session;
        }
    }

    /// <summary>
    /// Tries to return the generated workbook for a session, generating it on-demand when possible.
    /// </summary>
    public bool TryGetWorkbook(string sessionId, out byte[] bytes, out string fileName)
    {
        bytes = Array.Empty<byte>();
        fileName = $"azure-cost-estimate-{sessionId}.xlsx";
        var session = Get(sessionId);
        if (session is null)
            return false;

        EnsureWorkbookPersisted(session);

        var path = WorkbookPath(session.SessionId);
        lock (_ioLock)
        {
            if (!File.Exists(path))
                return false;

            bytes = File.ReadAllBytes(path);
        }
        if (!string.IsNullOrWhiteSpace(session.Scope?.ProjectName))
        {
            var safe = string.Concat(session.Scope.ProjectName.Split(Path.GetInvalidFileNameChars()));
            if (!string.IsNullOrWhiteSpace(safe))
                fileName = $"azure-cost-estimate-{safe}.xlsx";
        }
        return true;
    }

    private async Task RunScopeAsync(AgentSession session, CancellationToken ct)
    {
        var corpus = BaseFoundryAgent.BuildCorpus(session.Documents);
        if (_foundryOptions.IsConfigured)
        {
            try
            {
                session.Scope = await _scopeAgent.RunAsync(corpus, ct);
                session.Engine = "foundry";
                session.AgentSteps.Add(new AgentStepLog { Step = "scope", Summary = $"Foundry agent summarised scope: {session.Scope!.WorkloadProfile}." });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry scope agent failed for {SessionId}; using offline fallback.", session.SessionId);
                session.Scope = _offline.EstimateScope(session.Documents);
                session.Engine = "offline";
                session.AgentSteps.Add(new AgentStepLog { Step = "scope", Summary = $"Foundry scope agent failed ({ex.GetType().Name}); used deterministic offline scope: {session.Scope.WorkloadProfile}." });
                return;
            }
        }

        session.Scope = _offline.EstimateScope(session.Documents);
        session.Engine = "offline";
        session.AgentSteps.Add(new AgentStepLog { Step = "scope", Summary = $"Derived scope from {session.Documents.Count} document(s); workload profile: {session.Scope.WorkloadProfile}." });
    }

    private async Task RunRequirementsAsync(AgentSession session, CancellationToken ct)
    {
        if (session.Scope is null)
            throw new InvalidOperationException("Run Scope first before running Requirements.");

        var corpus = BaseFoundryAgent.BuildCorpus(session.Documents);
        if (_foundryOptions.IsConfigured)
        {
            try
            {
                session.Requirements = await _requirementsAgent.RunAsync(corpus, session.Scope, ct);
                session.Engine = "foundry";
                session.AgentSteps.Add(new AgentStepLog { Step = "requirements", Summary = $"Foundry agent derived {session.Requirements.Count} requirements." });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry requirements agent failed for {SessionId}; using offline fallback.", session.SessionId);
                session.Requirements = _offline.EstimateRequirements(session.Documents);
                session.Engine = "offline";
                session.AgentSteps.Add(new AgentStepLog { Step = "requirements", Summary = $"Foundry requirements agent failed ({ex.GetType().Name}); used deterministic offline requirements: {session.Requirements.Count} derived." });
                return;
            }
        }

        session.Requirements = _offline.EstimateRequirements(session.Documents);
        session.Engine = "offline";
        session.AgentSteps.Add(new AgentStepLog { Step = "requirements", Summary = $"Synthesized {session.Requirements.Count} technical requirements across {session.Requirements.Select(r => r.Category).Distinct().Count()} categories." });
    }

    private async Task RunFeaturesAsync(AgentSession session, CancellationToken ct)
    {
        if (session.Scope is null || session.Requirements.Count == 0)
            throw new InvalidOperationException("Run Background and Requirements first before running Features.");

        var corpus = BaseFoundryAgent.BuildCorpus(session.Documents);
        if (_foundryOptions.IsConfigured)
        {
            try
            {
                session.Features = await _featuresAgent.RunAsync(corpus, session.Scope, session.Requirements, ct);
                session.Engine = "foundry";
                session.AgentSteps.Add(new AgentStepLog { Step = "features", Summary = $"Foundry agent proposed {session.Features.Features.Count} features." });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry features agent failed for {SessionId}; using offline fallback.", session.SessionId);
                session.Features = _offline.EstimateFeatures(session.Scope, session.Requirements);
                session.Engine = "offline";
                session.AgentSteps.Add(new AgentStepLog { Step = "features", Summary = $"Foundry features agent failed ({ex.GetType().Name}); used deterministic offline feature list: {session.Features.Features.Count} derived." });
                return;
            }
        }

        session.Features = _offline.EstimateFeatures(session.Scope, session.Requirements);
        session.Engine = "offline";
        session.AgentSteps.Add(new AgentStepLog { Step = "features", Summary = $"Derived {session.Features.Features.Count} candidate features from scope and requirements." });
    }

    private async Task RunCostAsync(AgentSession session, CancellationToken ct)
    {
        if (session.Scope is null)
            throw new InvalidOperationException("Run Scope first before running Cost Model.");

        var corpus = BaseFoundryAgent.BuildCorpus(session.Documents);
        if (_foundryOptions.IsConfigured)
        {
            try
            {
                session.Cost = await _costModelAgent.RunAsync(corpus, session.Scope, session.CloudProvider, ct);
                session.Engine = "foundry";
                session.AgentSteps.Add(new AgentStepLog { Step = "cost", Summary = $"Foundry agent proposed {session.Cost.LineItems.Count} services; costed locally to {session.Cost.Currency} {session.Cost.MonthlyTotalWithContingency:N2}/mo (incl. contingency)." });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry cost agent failed for {SessionId}; using offline fallback.", session.SessionId);
                session.Cost = _offline.EstimateCost(session.Documents, session.CloudProvider);
                session.Engine = "offline";
                session.AgentSteps.Add(new AgentStepLog { Step = "cost", Summary = $"Foundry cost agent failed ({ex.GetType().Name}); used deterministic offline cost model: {session.Cost.MonthlyTotalWithContingency:N2}/mo." });
                return;
            }
        }

        session.Cost = _offline.EstimateCost(session.Documents, session.CloudProvider);
        session.Engine = "offline";
        session.AgentSteps.Add(new AgentStepLog { Step = "cost", Summary = $"Estimated {session.Cost.LineItems.Count} Azure line items. Monthly (incl. {session.Cost.ContingencyPercent}% contingency): {session.Cost.Currency} {session.Cost.MonthlyTotalWithContingency:N2}." });
    }

    private async Task RunProjectAsync(AgentSession session, CancellationToken ct)
    {
        if (session.Scope is null)
            throw new InvalidOperationException("Run Scope first before running Project Cost.");

        var corpus = BaseFoundryAgent.BuildCorpus(session.Documents);
        if (_foundryOptions.IsConfigured)
        {
            try
            {
                session.ProjectCost = await _projectCostAgent.RunAsync(corpus, session.Scope, ct);
                session.Engine = "foundry";
                session.AgentSteps.Add(new AgentStepLog { Step = "project", Summary = $"Foundry agent planned a {session.ProjectCost.Roles.Count}-role delivery team (~{session.ProjectCost.TotalDays:N0} person-days); build cost {session.ProjectCost.Currency} {session.ProjectCost.TotalWithContingency:N2} (incl. contingency)." });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry project agent failed for {SessionId}; using offline fallback.", session.SessionId);
                session.ProjectCost = _offline.EstimateProjectCost(session.Documents);
                session.Engine = "offline";
                session.AgentSteps.Add(new AgentStepLog { Step = "project", Summary = $"Foundry project-cost agent failed ({ex.GetType().Name}); used deterministic offline build plan: {session.ProjectCost.TotalWithContingency:N2}." });
                return;
            }
        }

        session.ProjectCost = _offline.EstimateProjectCost(session.Documents);
        session.Engine = "offline";
        session.AgentSteps.Add(new AgentStepLog { Step = "project", Summary = $"Planned a {session.ProjectCost.Roles.Count}-role delivery team (~{session.ProjectCost.TotalDays:N0} person-days). Build cost (incl. {session.ProjectCost.ContingencyPercent}% contingency): {session.ProjectCost.Currency} {session.ProjectCost.TotalWithContingency:N2}." });
    }

    private async Task RunOperationsAsync(AgentSession session, CancellationToken ct)
    {
        if (session.Scope is null)
            throw new InvalidOperationException("Run Scope first before running Operation Cost.");

        var corpus = BaseFoundryAgent.BuildCorpus(session.Documents);
        if (_foundryOptions.IsConfigured)
        {
            try
            {
                session.Operations = await _operationCostAgent.RunAsync(corpus, session.Scope, ct);
                session.Engine = "foundry";
                session.AgentSteps.Add(new AgentStepLog { Step = "operations", Summary = $"Foundry agent estimated {session.Operations.Items.Count} operating line items; monthly run cost {session.Operations.Currency} {session.Operations.MonthlyTotalWithContingency:N2} (incl. contingency)." });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry operations agent failed for {SessionId}; using offline fallback.", session.SessionId);
                session.Operations = _offline.EstimateOperations(session.Documents);
                session.Engine = "offline";
                session.AgentSteps.Add(new AgentStepLog { Step = "operations", Summary = $"Foundry operation-cost agent failed ({ex.GetType().Name}); used deterministic offline run model: {session.Operations.MonthlyTotalWithContingency:N2}/mo." });
                return;
            }
        }

        session.Operations = _offline.EstimateOperations(session.Documents);
        session.Engine = "offline";
        session.AgentSteps.Add(new AgentStepLog { Step = "operations", Summary = $"Estimated {session.Operations.Items.Count} ongoing operating line items. Monthly run cost (incl. {session.Operations.ContingencyPercent}% contingency): {session.Operations.Currency} {session.Operations.MonthlyTotalWithContingency:N2}." });
    }

    private async Task RunSpecAsync(AgentSession session, CancellationToken ct)
    {
        if (session.Scope is null)
            throw new InvalidOperationException("Run the Scope tab's Background step first before running Spec.");

        var buyCorpus = BaseFoundryAgent.BuildCorpus(session.BuyDocuments);
        if (_foundryOptions.IsConfigured)
        {
            try
            {
                session.Spec = await _specAgent.RunAsync(buyCorpus, session.Scope, ct);
                session.Engine = "foundry";
                session.AgentSteps.Add(new AgentStepLog { Step = "spec", Summary = $"Foundry agent summarised vendor spec for {session.Spec.VendorName}." });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry spec agent failed for {SessionId}; using offline fallback.", session.SessionId);
                session.Spec = _offline.EstimateBuySpec(session.BuyDocuments);
                session.Engine = "offline";
                session.AgentSteps.Add(new AgentStepLog { Step = "spec", Summary = $"Foundry spec agent failed ({ex.GetType().Name}); used deterministic offline spec summary." });
                return;
            }
        }

        session.Spec = _offline.EstimateBuySpec(session.BuyDocuments);
        session.Engine = "offline";
        session.AgentSteps.Add(new AgentStepLog { Step = "spec", Summary = $"Derived vendor spec summary from {session.BuyDocuments.Count} uploaded Buy document(s)." });
    }

    private async Task RunPurchaseAsync(AgentSession session, CancellationToken ct)
    {
        if (session.Scope is null || session.Spec is null)
            throw new InvalidOperationException("Run Scope and Spec first before running Purchase.");

        var buyCorpus = BaseFoundryAgent.BuildCorpus(session.BuyDocuments);
        if (_foundryOptions.IsConfigured)
        {
            try
            {
                session.Purchase = await _purchaseAgent.RunAsync(buyCorpus, session.Spec, session.Scope, ct);
                session.Engine = "foundry";
                session.AgentSteps.Add(new AgentStepLog { Step = "purchase", Summary = $"Foundry agent extracted {session.Purchase.Items.Count} purchase line items; one-time {session.Purchase.Currency} {session.Purchase.OneTimeTotalWithContingency:N2} + recurring {session.Purchase.RecurringAnnualTotalWithContingency:N2}/yr (incl. contingency)." });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry purchase agent failed for {SessionId}; using offline fallback.", session.SessionId);
                session.Purchase = _offline.EstimatePurchaseCost(session.BuyDocuments);
                session.Engine = "offline";
                session.AgentSteps.Add(new AgentStepLog { Step = "purchase", Summary = $"Foundry purchase agent failed ({ex.GetType().Name}); used deterministic offline purchase cost: {session.Purchase.Items.Count} line item(s)." });
                return;
            }
        }

        session.Purchase = _offline.EstimatePurchaseCost(session.BuyDocuments);
        session.Engine = "offline";
        session.AgentSteps.Add(new AgentStepLog { Step = "purchase", Summary = $"Extracted {session.Purchase.Items.Count} purchase line items from the uploaded Buy documents." });
    }

    private async Task RunBuyOperationsAsync(AgentSession session, CancellationToken ct)
    {
        if (session.Scope is null || session.Spec is null)
            throw new InvalidOperationException("Run Scope and Spec first before running Operation Cost.");

        var buyCorpus = BaseFoundryAgent.BuildCorpus(session.BuyDocuments);
        if (_foundryOptions.IsConfigured)
        {
            try
            {
                session.BuyOperations = await _buyOperationCostAgent.RunAsync(buyCorpus, session.Spec, session.Scope, ct);
                session.Engine = "foundry";
                session.AgentSteps.Add(new AgentStepLog { Step = "buyoperations", Summary = $"Foundry agent estimated {session.BuyOperations.Items.Count} Buy-option operating line items; monthly run cost {session.BuyOperations.Currency} {session.BuyOperations.MonthlyTotalWithContingency:N2} (incl. contingency)." });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry Buy operation-cost agent failed for {SessionId}; using offline fallback.", session.SessionId);
                session.BuyOperations = _offline.EstimateBuyOperations(session.BuyDocuments, session.Scope);
                session.Engine = "offline";
                session.AgentSteps.Add(new AgentStepLog { Step = "buyoperations", Summary = $"Foundry Buy operation-cost agent failed ({ex.GetType().Name}); used deterministic offline run model: {session.BuyOperations.MonthlyTotalWithContingency:N2}/mo." });
                return;
            }
        }

        session.BuyOperations = _offline.EstimateBuyOperations(session.BuyDocuments, session.Scope);
        session.Engine = "offline";
        session.AgentSteps.Add(new AgentStepLog { Step = "buyoperations", Summary = $"Estimated {session.BuyOperations.Items.Count} ongoing Buy-option operating line items. Monthly run cost (incl. {session.BuyOperations.ContingencyPercent}% contingency): {session.BuyOperations.Currency} {session.BuyOperations.MonthlyTotalWithContingency:N2}." });
    }

    private async Task RunCompareAsync(AgentSession session, CancellationToken ct)
    {
        if (session.Cost is null || session.ProjectCost is null || session.Operations is null)
            throw new InvalidOperationException("Run Cost Model, Project Cost, and Operation Cost before Compare.");

        session.Compare = await _comparisonService.CompareAsync(ToEstimationResult(session), ct);
        session.Engine = session.Compare.Engine;
        session.AgentSteps.Add(new AgentStepLog { Step = "compare", Summary = $"Completed Build-vs-Buy comparison with recommendation '{session.Compare.Recommendation}'." });
    }

    private void EnsureWorkbookPersisted(AgentSession session)
    {
        if (session.Cost is null || session.ProjectCost is null || session.Operations is null || session.Scope is null)
            return;

        var bytes = _excel.Generate(ToEstimationResult(session));
        lock (_ioLock)
        {
            File.WriteAllBytes(WorkbookPath(session.SessionId), bytes);
        }
    }

    private static AgentSessionSummary ToSummary(AgentSession session) => new()
    {
        SessionId = session.SessionId,
        CreatedUtc = session.CreatedUtc,
        UpdatedUtc = session.UpdatedUtc,
        Project = session.Scope?.ProjectName,
        Documents = session.Documents.Count,
        Status = OverallStatus(session)
    };

    private static string OverallStatus(AgentSession session)
    {
        EnsureStepDictionary(session);
        var statuses = session.Steps.Values.Select(s => s.Status).ToList();
        if (statuses.Contains("failed", StringComparer.OrdinalIgnoreCase)) return "failed";
        if (statuses.Contains("running", StringComparer.OrdinalIgnoreCase)) return "running";
        if (statuses.Count > 0 && statuses.All(s => string.Equals(s, "completed", StringComparison.OrdinalIgnoreCase))) return "completed";
        return "pending";
    }

    private static void EnsurePrerequisites(AgentSession session, string step)
    {
        if (step is "requirements" or "cost" or "project" or "operations" && session.Scope is null)
            throw new InvalidOperationException("Run Scope first before running this step.");

        if (step == "features" && (session.Scope is null || session.Requirements.Count == 0))
            throw new InvalidOperationException("Run Background and Requirements first before running Features.");

        if (step == "spec" && session.Scope is null)
            throw new InvalidOperationException("Run the Scope tab's Background step first before running Spec.");

        if (step is "purchase" or "buyoperations" && (session.Scope is null || session.Spec is null))
            throw new InvalidOperationException("Run Scope and Spec first before running this step.");

        if (step == "compare" && (session.Cost is null || session.ProjectCost is null || session.Operations is null))
            throw new InvalidOperationException("Run Cost Model, Project Cost, and Operation Cost before Compare.");
    }

    private static void EnsureStepDictionary(AgentSession session)
    {
        session.Steps ??= AgentSession.CreateDefaultSteps();
        foreach (var step in AgentSession.StepOrder)
        {
            if (!session.Steps.ContainsKey(step))
                session.Steps[step] = new StepState();
        }
    }

    private EstimationResult ToEstimationResult(AgentSession session) => new()
    {
        JobId = session.SessionId,
        CreatedUtc = session.CreatedUtc,
        Status = OverallStatus(session),
        Engine = session.Engine,
        Documents = session.Documents,
        Scope = session.Scope ?? new ScopeSummary(),
        Requirements = session.Requirements,
        Features = session.Features ?? new FeatureSet(),
        Cost = session.Cost ?? new CostEstimate(),
        ProjectCost = session.ProjectCost ?? new ProjectBuildCost(),
        Operations = session.Operations ?? new OperationCost(),
        BuyDocuments = session.BuyDocuments,
        Spec = session.Spec ?? new BuySpecSummary(),
        Purchase = session.Purchase ?? new PurchaseCost(),
        BuyOperations = session.BuyOperations ?? new OperationCost(),
        AgentSteps = session.AgentSteps
    };

    private async Task<string> CreateSessionIdAsync(CancellationToken ct)
    {
        while (true)
        {
            var candidate = $"session-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            if (!Directory.Exists(SessionDirectory(candidate)))
                return candidate;

            await Task.Delay(2, ct);
        }
    }

    private void Persist(AgentSession session)
    {
        EnsureStepDictionary(session);
        _cache[session.SessionId] = session;
        var dir = SessionDirectory(session.SessionId);
        Directory.CreateDirectory(dir);
        lock (_ioLock)
        {
            File.WriteAllText(SessionJsonPath(session.SessionId), JsonSerializer.Serialize(session, JsonOpts));
        }
    }

    private void LoadExisting()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_dataDir, "session-*"))
            {
                var sessionId = Path.GetFileName(dir);
                if (!IsValidSessionId(sessionId))
                    continue;

                var jsonPath = Path.Combine(dir, "session.json");
                if (!File.Exists(jsonPath))
                    continue;

                try
                {
                    var session = JsonSerializer.Deserialize<AgentSession>(File.ReadAllText(jsonPath), JsonOpts);
                    if (session is null)
                        continue;

                    EnsureStepDictionary(session);
                    _cache[session.SessionId] = session;
                }
                catch
                {
                    // Skip malformed sessions and keep loading the rest.
                }
            }

            _logger.LogInformation("Loaded {Count} existing session(s) from {Dir}", _cache.Count, _dataDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load existing sessions from {Dir}", _dataDir);
        }
    }

    private static bool IsValidSessionId(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        !sessionId.Contains("..", StringComparison.Ordinal) &&
        !sessionId.Contains(Path.DirectorySeparatorChar) &&
        !sessionId.Contains(Path.AltDirectorySeparatorChar) &&
        SessionIdPattern().IsMatch(sessionId);

    private string SessionDirectory(string sessionId) => Path.Combine(_dataDir, sessionId);
    private string SessionJsonPath(string sessionId) => Path.Combine(SessionDirectory(sessionId), "session.json");
    private string WorkbookPath(string sessionId) => Path.Combine(SessionDirectory(sessionId), "workbook.xlsx");
}
