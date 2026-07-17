using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services.Foundry;

/// <summary>
/// Estimation engine backed by a Microsoft Foundry prompt agent (Microsoft Agent Framework, hosted
/// in-process pattern via <c>AIProjectClient.AsAIAgent(...)</c>).
///
/// Pipeline (three grounded prompt-agent calls, each returning JSON):
///   1. SCOPE        — read the documents, summarise scope/workload/scale/data-sensitivity.
///   2. REQUIREMENTS — derive technical requirements from the scope + documents.
///   3. SERVICES     — propose the concrete Azure service plan (services, SKUs, quantities).
/// The proposed service plan is then costed locally via <see cref="AzurePricingCatalog"/> so the
/// arithmetic is deterministic and auditable (the model decides architecture; we own the math).
///
/// On ANY failure (missing config, auth, transient service error) it transparently falls back to the
/// offline engine and records the reason, so the POC is always demonstrable.
/// </summary>
public sealed class FoundryEstimationEngine : IEstimationEngine
{
    private readonly FoundryOptions _options;
    private readonly OfflineEstimationEngine _offline;
    private readonly ILogger<FoundryEstimationEngine> _logger;

    public FoundryEstimationEngine(FoundryOptions options, OfflineEstimationEngine offline, ILogger<FoundryEstimationEngine> logger)
    {
        _options = options;
        _offline = offline;
        _logger = logger;
    }

    public string Name => "foundry";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

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
            var agent = CreateAgent();
            var corpus = BuildCorpus(job);

            // 1) SCOPE
            var scope = await RunJsonAsync<ScopeSummary>(agent, ScopePrompt(corpus), ct);
            job.Scope = scope ?? throw new InvalidOperationException("Scope step returned no JSON.");
            NormalizeScope(job.Scope);
            job.AgentSteps.Add(new AgentStepLog { Step = "scope", Summary = $"Foundry agent summarised scope: {job.Scope.WorkloadProfile}." });

            // 2) REQUIREMENTS
            var reqWrap = await RunJsonAsync<RequirementsWrapper>(agent, RequirementsPrompt(corpus, job.Scope), ct);
            job.Requirements = reqWrap?.Requirements ?? new();
            RenumberRequirements(job.Requirements);
            job.AgentSteps.Add(new AgentStepLog { Step = "requirements", Summary = $"Foundry agent derived {job.Requirements.Count} requirements." });

            // 3) SERVICE PLAN -> local costing
            var plan = await RunJsonAsync<ServicePlan>(agent, ServicePlanPrompt(corpus, job.Scope), ct);
            job.Cost = CostFromPlan(plan, job.Scope);
            job.AgentSteps.Add(new AgentStepLog { Step = "cost", Summary = $"Foundry agent proposed {job.Cost.LineItems.Count} services; costed locally to {job.Cost.Currency} {job.Cost.MonthlyTotalWithContingency:N2}/mo (incl. contingency)." });

            // 4) PROJECT (build) COST
            var buildPlan = await RunJsonAsync<BuildPlan>(agent, ProjectCostPrompt(corpus, job.Scope), ct);
            job.ProjectCost = ProjectCostFromPlan(buildPlan);
            job.AgentSteps.Add(new AgentStepLog { Step = "project", Summary = $"Foundry agent planned a {job.ProjectCost.Roles.Count}-role delivery team (~{job.ProjectCost.TotalDays:N0} person-days); build cost {job.ProjectCost.Currency} {job.ProjectCost.TotalWithContingency:N2} (incl. contingency)." });

            // 5) OPERATION (run) COST
            var opsPlan = await RunJsonAsync<OperationsPlan>(agent, OperationsPrompt(corpus, job.Scope), ct);
            job.Operations = OperationsFromPlan(opsPlan);
            job.AgentSteps.Add(new AgentStepLog { Step = "operations", Summary = $"Foundry agent estimated {job.Operations.Items.Count} operating line items; monthly run cost {job.Operations.Currency} {job.Operations.MonthlyTotalWithContingency:N2} (incl. contingency)." });

            job.Engine = Name;
            job.Status = "completed";
            return job;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Foundry estimation failed; falling back to offline engine.");
            // Reset partial state and fall back so the user still gets a complete result.
            job.Scope = new();
            job.Requirements = new();
            job.Cost = new();
            job.ProjectCost = new();
            job.Operations = new();
            job.AgentSteps.Clear();
            await _offline.EstimateAsync(job, ct);
            job.AgentSteps.Insert(0, new AgentStepLog { Step = "engine", Summary = $"Foundry call failed ({ex.GetType().Name}); fell back to offline engine. Detail: {Trunc(ex.Message, 200)}" });
            return job;
        }
    }

    private AIAgent CreateAgent()
    {
        // Exclude the dev-machine credential sources (Visual Studio, VS Code, Azure PowerShell, azd)
        // that can silently resolve to a different signed-in account than `az login`, causing 403s.
        // Managed Identity (App Service) and Azure CLI remain enabled so the same code works locally
        // and when deployed.
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
        var credential = new DefaultAzureCredential(credOptions);
        var client = new AIProjectClient(new Uri(_options.ProjectEndpoint!), credential);
        return client.AsAIAgent(
            model: _options.ModelDeploymentName,
            instructions: AgentInstructions.SystemPersona,
            name: _options.AgentName);
    }

    private static string BuildCorpus(EstimationResult job)
    {
        var docs = string.Join("\n\n", job.Documents.Select(d => $"=== FILE: {d.FileName} ({d.WordCount} words) ===\n{d.ExtractedText}"));
        return Trunc(docs, 48_000);
    }

    private async Task<T?> RunJsonAsync<T>(AIAgent agent, string prompt, CancellationToken ct)
    {
        var response = await agent.RunAsync(prompt, cancellationToken: ct);
        var text = response.Text ?? string.Empty;
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json)) return default;
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    /// <summary>Robustly pull the first {...} JSON object out of model output (handles stray fences/prose).</summary>
    private static string? ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text.Substring(start, end - start + 1);
    }

    // ---------------- Prompts ----------------

    private static string ScopePrompt(string corpus) =>
        $$"""
        Analyze the following technical document(s) and produce a SCOPE summary.

        Return JSON with exactly these fields:
        {
          "projectName": string,
          "overview": string,
          "businessGoal": string,
          "inScope": string[],
          "outOfScope": string[],
          "assumptions": string[],
          "workloadProfile": string,        // e.g. "web front end + API + AI workload + relational data store"
          "expectedScale": string,          // e.g. "~5k MAU, ~50 req/s peak"
          "dataSensitivity": string,        // e.g. "internal business data" | "contains PII" | "regulated"
          "environment": string             // "production" | "non-production (POC/dev)"
        }

        DOCUMENTS:
        {{corpus}}
        """;

    private static string RequirementsPrompt(string corpus, ScopeSummary scope) =>
        $$"""
        Given this SCOPE and the source documents, derive the technical requirements for an Azure solution.

        SCOPE: {{JsonSerializer.Serialize(scope, JsonOpts)}}

        Return JSON: { "requirements": [ {
          "id": "REQ-001",
          "category": "Compute|Data|Networking|Security|AI|Observability",
          "requirement": string,
          "rationale": string,
          "priority": "Must|Should|Could"
        } ] }

        Cover compute, data, AI/Foundry (if relevant), security (managed identity, Key Vault), networking
        (HTTPS-only), and observability. 8-14 requirements.

        DOCUMENTS:
        {{corpus}}
        """;

    private static string ServicePlanPrompt(string corpus, ScopeSummary scope) =>
        $$"""
        Design the concrete Azure service plan to run this workload for ONE month, then estimate quantities.
        You decide services/SKUs/quantities; do NOT compute dollar costs (we price them separately).

        SCOPE: {{JsonSerializer.Serialize(scope, JsonOpts)}}

        Return JSON: { "services": [ {
          "service": string,            // e.g. "Azure App Service"
          "sku": string,                // e.g. "P1v3"
          "category": "Compute|AI|Data|Networking|Security|Observability",
          "meter": string,              // e.g. "instance-month", "1K input tokens", "GB-month"
          "assumption": string,         // sizing rationale
          "quantity": number,           // monthly PRODUCTION quantity for the meter
          "nonProdQuantity": number,    // monthly NON-PROD (dev/test) quantity for the same meter (scaled-down)
          "unitPrice": number,          // your best-estimate USD reference unit price for the meter
          "unit": string,               // e.g. "per instance/mo", "per 1K tokens", "per GB/mo"
          "pricingReferenceUrl": string,// first-party Azure pricing page for this service (azure.microsoft.com/pricing/details/...)
          "pricingReferenceLabel": string // short label, e.g. "App Service pricing"
        } ],
          "contingencyPercent": number  // 15-30, risk buffer
        }

        Always include: compute (App Service), storage (Blob), observability (Log Analytics), security
        (Key Vault). Include Foundry/Azure OpenAI token line items if the workload uses AI, and Azure AI
        Search if it uses document/file search. For pricingReferenceUrl, cite the official Microsoft Azure
        pricing details page for that service so each line item is auditable.

        DOCUMENTS:
        {{corpus}}
        """;

    private static string ProjectCostPrompt(string corpus, ScopeSummary scope) =>
        $$"""
        Plan the delivery team and effort to BUILD this solution (one-time cost). You choose the roles,
        their day rates, and person-days; do NOT compute dollar totals (we multiply rate × days).

        SCOPE: {{JsonSerializer.Serialize(scope, JsonOpts)}}

        Return JSON: { "roles": [ {
          "role": string,               // e.g. "Solution Architect", "Backend Developer", "QA Engineer", "Project Manager"
          "description": string,        // one line on what this role delivers on this project
          "dayRate": number,            // reference USD day rate for the role
          "estimatedDays": number       // person-days of effort for this role on this build
        } ],
          "contingencyPercent": number  // 10-25, delivery risk buffer
        }

        Always include a Solution Architect, Project Manager, and QA Engineer. Add Backend, Frontend,
        AI/ML, Data, and DevOps roles only when the scope/requirements call for them. Scale the person-days
        to complexity and expected scale — a small POC is a few weeks; an enterprise build is much larger.

        DOCUMENTS:
        {{corpus}}
        """;

    private static string OperationsPrompt(string corpus, ScopeSummary scope) =>
        $$"""
        Estimate the ONGOING monthly cost to run, support, and maintain this solution after go-live
        (separate from Azure infrastructure and from the one-time build). You choose the activities and
        sizing; do NOT compute dollar totals (we multiply quantity × unit price).

        SCOPE: {{JsonSerializer.Serialize(scope, JsonOpts)}}

        Return JSON: { "items": [ {
          "item": string,               // e.g. "Application support (L2/L3)", "Monitoring & incident response"
          "description": string,        // what the activity covers
          "category": "Support|Maintenance|Operations|Licensing",
          "cadence": string,            // informational, e.g. "Monthly"
          "quantity": number,           // monthly quantity for the meter (e.g. hours/mo)
          "unitPrice": number,          // reference USD unit price (e.g. per hour)
          "unit": string                // e.g. "per hour", "per month"
        } ],
          "contingencyPercent": number  // 10-25, operating risk buffer
        }

        Always include application support, monitoring & incident response, software updates & patching,
        and minor enhancements. Add security & compliance reviews when data is PII/regulated, and AI model
        monitoring / prompt tuning when the workload uses AI. 4-8 line items.

        DOCUMENTS:
        {{corpus}}
        """;

    // ---------------- Plan -> Cost ----------------

    private static CostEstimate CostFromPlan(ServicePlan? plan, ScopeSummary scope)
    {
        var est = new CostEstimate
        {
            Currency = AzurePricingCatalog.Currency,
            Region = AzurePricingCatalog.Region,
            PricingBasis = "Foundry-proposed plan, priced with POC reference rates",
            ContingencyPercent = plan?.ContingencyPercent is >= 10 and <= 40 ? plan.ContingencyPercent : 20m,
            Notes =
            {
                "Service plan proposed by Microsoft Foundry prompt agent; unit prices are reference estimates.",
                "Validate against the Azure Pricing Calculator / Retail Prices API before commitment.",
                "Each line item links to its first-party Azure pricing page for audit (shown in UI and Excel).",
                "Non-prod view models a scaled-down dev/test footprint of the same architecture; Total = Non-prod + Prod.",
                $"Scale assumption: {scope.ExpectedScale}"
            }
        };

        var services = plan?.Services ?? new();
        foreach (var sp in services)
        {
            var qty = sp.Quantity;
            // Prefer our catalog price for known App Service SKUs to keep numbers grounded; otherwise trust the model.
            var unit = sp.UnitPrice;
            if (sp.Service.Contains("App Service", StringComparison.OrdinalIgnoreCase) &&
                AzurePricingCatalog.AppServicePlanMonthly.TryGetValue(sp.Sku, out var catalogPrice))
            {
                unit = catalogPrice;
            }
            // Prefer the model's non-prod sizing when it provided a sensible value, else derive from the catalog factor.
            var nonProdQty = sp.NonProdQuantity is { } npq && npq >= 0 && npq <= qty
                ? Math.Round(npq, 4)
                : Math.Round(qty * AzurePricingCatalog.NonProdFactor(sp.Category), 4);
            // Prefer a model-supplied first-party pricing link, else resolve from the catalog.
            var catalogRef = AzurePricingCatalog.ResolvePricingReference(sp.Service);
            var refUrl = !string.IsNullOrWhiteSpace(sp.PricingReferenceUrl) && sp.PricingReferenceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? sp.PricingReferenceUrl
                : catalogRef.Url;
            var refLabel = !string.IsNullOrWhiteSpace(sp.PricingReferenceLabel) ? sp.PricingReferenceLabel : catalogRef.Label;
            est.LineItems.Add(new CostLineItem
            {
                Service = sp.Service,
                Sku = sp.Sku,
                Meter = sp.Meter,
                Assumption = sp.Assumption,
                Quantity = qty,
                UnitPrice = unit,
                Unit = sp.Unit,
                Category = string.IsNullOrWhiteSpace(sp.Category) ? "Other" : sp.Category,
                MonthlyCost = Math.Round(qty * unit, 2),
                NonProdQuantity = nonProdQty,
                PricingReferenceUrl = refUrl,
                PricingReferenceLabel = refLabel
            });
        }

        if (est.LineItems.Count == 0)
        {
            est.Notes.Add("Foundry returned no service line items; supplementing with baseline storage/observability.");
            est.LineItems.Add(new CostLineItem { Service = "Azure Blob Storage", Sku = "Hot LRS", Meter = "GB-month", Assumption = "Baseline artefact storage", Quantity = 20, UnitPrice = AzurePricingCatalog.BlobHotPerGbMonth, Unit = "per GB/mo", Category = "Data", MonthlyCost = Math.Round(20 * AzurePricingCatalog.BlobHotPerGbMonth, 2), NonProdQuantity = Math.Round(20 * AzurePricingCatalog.NonProdFactor("Data"), 4), PricingReferenceUrl = AzurePricingCatalog.ResolvePricingReference("Azure Blob Storage").Url, PricingReferenceLabel = AzurePricingCatalog.ResolvePricingReference("Azure Blob Storage").Label });
        }

        return est;
    }

    // ---------------- Plan -> Project (build) cost ----------------

    private static ProjectBuildCost ProjectCostFromPlan(BuildPlan? plan)
    {
        var est = new ProjectBuildCost
        {
            Currency = AzurePricingCatalog.Currency,
            ContingencyPercent = plan?.ContingencyPercent is >= 5 and <= 40 ? plan.ContingencyPercent : 15m,
            Notes =
            {
                "Delivery plan proposed by Microsoft Foundry prompt agent; day rates and effort are reference estimates.",
                "One-time cost to design and build the solution (delivery team effort), separate from Azure run cost.",
                "Edit day rates / person-days to re-plan the team; validate against an actual statement of work."
            }
        };

        foreach (var role in plan?.Roles ?? new())
        {
            est.Roles.Add(new ProjectRoleLineItem
            {
                Role = string.IsNullOrWhiteSpace(role.Role) ? "Delivery role" : role.Role,
                Description = role.Description,
                DayRate = role.DayRate > 0 ? role.DayRate : 900m,
                EstimatedDays = role.EstimatedDays > 0 ? role.EstimatedDays : 5m
            });
        }

        if (est.Roles.Count == 0)
        {
            est.Notes.Add("Foundry returned no delivery roles; supplementing with a baseline core team.");
            est.Roles.Add(new ProjectRoleLineItem { Role = "Solution Architect", Description = "Solution design and Azure architecture.", DayRate = 1200m, EstimatedDays = 10m });
            est.Roles.Add(new ProjectRoleLineItem { Role = "Backend Developer", Description = "APIs, services, and integration.", DayRate = 900m, EstimatedDays = 25m });
            est.Roles.Add(new ProjectRoleLineItem { Role = "QA Engineer", Description = "Test planning and release verification.", DayRate = 750m, EstimatedDays = 15m });
            est.Roles.Add(new ProjectRoleLineItem { Role = "Project Manager", Description = "Delivery planning and coordination.", DayRate = 1000m, EstimatedDays = 14m });
        }

        return est;
    }

    // ---------------- Plan -> Operation (run) cost ----------------

    private static OperationCost OperationsFromPlan(OperationsPlan? plan)
    {
        var est = new OperationCost
        {
            Currency = AzurePricingCatalog.Currency,
            ContingencyPercent = plan?.ContingencyPercent is >= 5 and <= 40 ? plan.ContingencyPercent : 15m,
            Notes =
            {
                "Operating plan proposed by Microsoft Foundry prompt agent; quantities and rates are reference estimates.",
                "Ongoing monthly cost to run, support, and maintain the solution (excludes Azure infra and the one-time build).",
                "Edit quantities / unit prices to adjust the operating model; validate against an actual support agreement."
            }
        };

        foreach (var it in plan?.Items ?? new())
        {
            est.Items.Add(new OperationCostLineItem
            {
                Item = string.IsNullOrWhiteSpace(it.Item) ? "Operating activity" : it.Item,
                Description = it.Description,
                Category = string.IsNullOrWhiteSpace(it.Category) ? "Operations" : it.Category,
                Cadence = string.IsNullOrWhiteSpace(it.Cadence) ? "Monthly" : it.Cadence,
                Quantity = it.Quantity > 0 ? it.Quantity : 1m,
                UnitPrice = it.UnitPrice > 0 ? it.UnitPrice : 120m,
                Unit = string.IsNullOrWhiteSpace(it.Unit) ? "per hour" : it.Unit
            });
        }

        if (est.Items.Count == 0)
        {
            est.Notes.Add("Foundry returned no operating line items; supplementing with a baseline run-support model.");
            est.Items.Add(new OperationCostLineItem { Item = "Application support (L2/L3)", Description = "Triage, bug fixes, and user support.", Category = "Support", Cadence = "Monthly", Quantity = 16m, UnitPrice = 120m, Unit = "per hour" });
            est.Items.Add(new OperationCostLineItem { Item = "Monitoring & incident response", Description = "Health monitoring and incident handling.", Category = "Operations", Cadence = "Monthly", Quantity = 8m, UnitPrice = 130m, Unit = "per hour" });
            est.Items.Add(new OperationCostLineItem { Item = "Software updates & patching", Description = "Dependency updates and security patching.", Category = "Maintenance", Cadence = "Monthly", Quantity = 6m, UnitPrice = 120m, Unit = "per hour" });
        }

        return est;
    }

    private static void NormalizeScope(ScopeSummary s)
    {
        s.ProjectName = string.IsNullOrWhiteSpace(s.ProjectName) ? "Untitled POC" : s.ProjectName;
        s.WorkloadProfile = string.IsNullOrWhiteSpace(s.WorkloadProfile) ? "web workload" : s.WorkloadProfile;
        s.Environment = string.IsNullOrWhiteSpace(s.Environment) ? "production" : s.Environment;
    }

    private static void RenumberRequirements(List<TechnicalRequirement> reqs)
    {
        for (int i = 0; i < reqs.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(reqs[i].Id)) reqs[i].Id = $"REQ-{i + 1:000}";
            if (string.IsNullOrWhiteSpace(reqs[i].Priority)) reqs[i].Priority = "Should";
            if (string.IsNullOrWhiteSpace(reqs[i].Category)) reqs[i].Category = "Other";
        }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---------------- DTOs for model JSON ----------------

    private sealed class RequirementsWrapper
    {
        [JsonPropertyName("requirements")] public List<TechnicalRequirement> Requirements { get; set; } = new();
    }

    private sealed class ServicePlan
    {
        [JsonPropertyName("services")] public List<ServicePlanItem> Services { get; set; } = new();
        [JsonPropertyName("contingencyPercent")] public decimal ContingencyPercent { get; set; } = 20m;
    }

    private sealed class ServicePlanItem
    {
        [JsonPropertyName("service")] public string Service { get; set; } = "";
        [JsonPropertyName("sku")] public string Sku { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("meter")] public string Meter { get; set; } = "";
        [JsonPropertyName("assumption")] public string Assumption { get; set; } = "";
        [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
        [JsonPropertyName("nonProdQuantity")] public decimal? NonProdQuantity { get; set; }
        [JsonPropertyName("unitPrice")] public decimal UnitPrice { get; set; }
        [JsonPropertyName("unit")] public string Unit { get; set; } = "";
        [JsonPropertyName("pricingReferenceUrl")] public string? PricingReferenceUrl { get; set; }
        [JsonPropertyName("pricingReferenceLabel")] public string? PricingReferenceLabel { get; set; }
    }

    private sealed class BuildPlan
    {
        [JsonPropertyName("roles")] public List<BuildPlanRole> Roles { get; set; } = new();
        [JsonPropertyName("contingencyPercent")] public decimal ContingencyPercent { get; set; } = 15m;
    }

    private sealed class BuildPlanRole
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("dayRate")] public decimal DayRate { get; set; }
        [JsonPropertyName("estimatedDays")] public decimal EstimatedDays { get; set; }
    }

    private sealed class OperationsPlan
    {
        [JsonPropertyName("items")] public List<OperationsPlanItem> Items { get; set; } = new();
        [JsonPropertyName("contingencyPercent")] public decimal ContingencyPercent { get; set; } = 15m;
    }

    private sealed class OperationsPlanItem
    {
        [JsonPropertyName("item")] public string Item { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("cadence")] public string Cadence { get; set; } = "";
        [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
        [JsonPropertyName("unitPrice")] public decimal UnitPrice { get; set; }
        [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    }
}
