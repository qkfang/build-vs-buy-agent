using System.Text;
using System.Text.RegularExpressions;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Deterministic, signal-based estimation engine. It does not call any external model, so it always
/// works (CI, offline, no Azure access). It inspects the ingested document text for keyword signals
/// and translates them into a scope summary, technical requirements, and a costed Azure architecture
/// using <see cref="AzurePricingCatalog"/>.
///
/// The Foundry-backed engine reuses this engine's catalog math to turn the model's structured
/// service plan into final numbers, so cost arithmetic is consistent and auditable in both modes.
/// </summary>
public sealed partial class OfflineEstimationEngine : IEstimationEngine
{
    public string Name => "offline";

    public Task<EstimationResult> EstimateAsync(EstimationResult job, CancellationToken ct = default)
    {
        var corpus = string.Join("\n\n", job.Documents.Select(d => $"# {d.FileName}\n{d.ExtractedText}"));
        var signals = WorkloadSignals.FromText(corpus);

        job.Engine = Name;
        job.Scope = BuildScope(job, signals, corpus);
        job.AgentSteps.Add(new AgentStepLog { Step = "scope", Summary = $"Derived scope from {job.Documents.Count} document(s); workload profile: {job.Scope.WorkloadProfile}." });

        job.Requirements = BuildRequirements(signals);
        job.AgentSteps.Add(new AgentStepLog { Step = "requirements", Summary = $"Synthesized {job.Requirements.Count} technical requirements across {job.Requirements.Select(r => r.Category).Distinct().Count()} categories." });

        job.Cost = BuildCost(signals);
        job.AgentSteps.Add(new AgentStepLog { Step = "cost", Summary = $"Estimated {job.Cost.LineItems.Count} Azure line items. Monthly (incl. {job.Cost.ContingencyPercent}% contingency): {job.Cost.Currency} {job.Cost.MonthlyTotalWithContingency:N2}." });

        job.ProjectCost = BuildProjectCost(signals);
        job.AgentSteps.Add(new AgentStepLog { Step = "project", Summary = $"Planned a {job.ProjectCost.Roles.Count}-role delivery team (~{job.ProjectCost.TotalDays:N0} person-days). Build cost (incl. {job.ProjectCost.ContingencyPercent}% contingency): {job.ProjectCost.Currency} {job.ProjectCost.TotalWithContingency:N2}." });

        job.Operations = BuildOperations(signals);
        job.AgentSteps.Add(new AgentStepLog { Step = "operations", Summary = $"Estimated {job.Operations.Items.Count} ongoing operating line items. Monthly run cost (incl. {job.Operations.ContingencyPercent}% contingency): {job.Operations.Currency} {job.Operations.MonthlyTotalWithContingency:N2}." });

        job.Status = "completed";
        return Task.FromResult(job);
    }

    // ---------------------------------------------------------------- Scope

    private static ScopeSummary BuildScope(EstimationResult job, WorkloadSignals s, string corpus)
    {
        var projectName = GuessProjectName(job, corpus);
        var profileParts = new List<string>();
        if (s.HasWebApp) profileParts.Add("web front end");
        if (s.HasApi) profileParts.Add("API/back end");
        if (s.HasAi) profileParts.Add("AI/LLM workload");
        if (s.HasRelationalDb) profileParts.Add("relational data store");
        if (s.HasNoSql) profileParts.Add("NoSQL data store");
        if (s.HasFunctions) profileParts.Add("event/serverless processing");
        if (profileParts.Count == 0) profileParts.Add("general web workload");

        return new ScopeSummary
        {
            ProjectName = projectName,
            Overview = BuildOverview(s, profileParts),
            BusinessGoal = s.HasAi
                ? "Deliver an AI-assisted capability that automates document understanding and decisioning."
                : "Deliver a cloud-hosted application that meets the documented functional needs.",
            WorkloadProfile = string.Join(" + ", profileParts),
            ExpectedScale = s.DescribeScale(),
            DataSensitivity = s.DescribeDataSensitivity(),
            Environment = s.IsProduction ? "production" : "non-production (POC/dev)",
            InScope = BuildInScope(s),
            OutOfScope = new List<string>
            {
                "Net-new custom model training (uses pre-built/Foundry models)",
                "On-premises integration beyond documented connectors",
                "24x7 managed operations (assumes platform-managed SLAs)"
            },
            Assumptions = new List<string>
            {
                $"Primary region is {AzurePricingCatalog.Region}; single-region deployment for the POC.",
                "Pay-As-You-Go list pricing; no Reserved Instances or savings plans applied.",
                "Reference rates are POC approximations, not a binding quote.",
                $"{s.ContingencyPercent}% contingency applied to absorb estimation uncertainty."
            }
        };
    }

    private static string BuildOverview(WorkloadSignals s, List<string> profileParts)
    {
        var sb = new StringBuilder();
        sb.Append("Based on the supplied technical documentation, the solution is a ");
        sb.Append(string.Join(", ", profileParts));
        sb.Append('.');
        if (s.HasAi)
            sb.Append(" It applies generative-AI document understanding to extract scope and drive automated estimation.");
        if (s.HasRelationalDb || s.HasNoSql)
            sb.Append(" Persistent storage is required for application and/or reference data.");
        return sb.ToString();
    }

    private static List<string> BuildInScope(WorkloadSignals s)
    {
        var list = new List<string>();
        if (s.HasWebApp) list.Add("Hosted web application (Azure App Service)");
        if (s.HasApi) list.Add("HTTP API surface for integration");
        if (s.HasAi) list.Add("Foundry prompt agent for document understanding and reasoning");
        if (s.HasFileSearch) list.Add("Document ingestion with vector/file search grounding");
        if (s.HasRelationalDb) list.Add("Relational database (Azure SQL)");
        if (s.HasNoSql) list.Add("NoSQL store (Azure Cosmos DB)");
        if (s.HasFunctions) list.Add("Serverless background processing (Azure Functions)");
        list.Add("Blob storage for uploads and generated artefacts");
        list.Add("Centralised observability (Application Insights + Log Analytics)");
        list.Add("Secrets management (Azure Key Vault) and managed identity");
        return list;
    }

    private static string GuessProjectName(EstimationResult job, string corpus)
    {
        var docNames = job.Documents
            .Select(d => Path.GetFileNameWithoutExtension(d.FileName).Trim())
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        static string StripProjectLabel(string s) =>
            Regex.Replace(s, @"(?i)^\s*project\s*[:\-]\s*", "").Trim();

        // A heading is just the injected filename line if it matches a document name (with or without extension).
        bool IsFileNameHeading(string h)
        {
            var bare = Path.GetFileNameWithoutExtension(h).Trim();
            return docNames.Contains(h) || docNames.Contains(bare);
        }

        // Prefer an explicit "Project:" / title line.
        var m = ProjectLineRegex().Match(corpus);
        if (m.Success && m.Groups[1].Value.Trim().Length > 2)
            return Truncate(StripProjectLabel(m.Groups[1].Value), 80);

        // Else the first markdown heading that is not just the injected filename line.
        foreach (Match h in HeadingRegex().Matches(corpus))
        {
            var raw = h.Groups[1].Value.Trim();
            if (IsFileNameHeading(raw)) continue;
            var heading = StripProjectLabel(raw);
            if (heading.Length > 2)
                return Truncate(heading, 80);
        }

        var first = job.Documents.FirstOrDefault();
        return first is null ? "Untitled POC" : Path.GetFileNameWithoutExtension(first.FileName);
    }

    // ---------------------------------------------------------------- Requirements

    private static List<TechnicalRequirement> BuildRequirements(WorkloadSignals s)
    {
        var reqs = new List<TechnicalRequirement>();
        int n = 1;
        void Add(string cat, string req, string why, string prio) =>
            reqs.Add(new TechnicalRequirement { Id = $"REQ-{n++:000}", Category = cat, Requirement = req, Rationale = why, Priority = prio });

        if (s.HasWebApp)
            Add("Compute", $"Host the web tier on Azure App Service ({s.RecommendedAppServiceSku} Linux).",
                "Managed PaaS hosting with autoscale, TLS, and zero-downtime deploys.", "Must");
        if (s.HasApi)
            Add("Compute", "Expose application capabilities via an HTTP API (optionally as an OpenAPI tool for Foundry agents).",
                "Enables integration and agent tool-calling per Foundry OpenAPI pattern.", "Should");
        if (s.HasAi)
        {
            Add("AI", $"Use a Microsoft Foundry prompt agent on model deployment '{s.PreferredModel}' for document understanding and estimation reasoning.",
                "Centralised, governed model access with content safety and identity.", "Must");
            Add("AI", "Apply structured outputs (JSON schema) for scope, requirements, and cost so results are machine-validated.",
                "Deterministic, parseable agent output for downstream Excel generation.", "Must");
        }
        if (s.HasFileSearch)
            Add("AI", "Ingest uploaded documents into a Foundry vector store and enable the file search tool for grounding.",
                "Grounds answers in customer documents and reduces hallucination.", "Should");
        if (s.HasRelationalDb)
            Add("Data", "Provision Azure SQL Database (General Purpose serverless) for transactional data.",
                "Relational integrity with cost-efficient auto-pause for POC workloads.", "Must");
        if (s.HasNoSql)
            Add("Data", "Provision Azure Cosmos DB (serverless) for high-throughput document/key-value data.",
                "Elastic, low-latency NoSQL with consumption billing.", "Should");
        if (s.HasFunctions)
            Add("Compute", "Implement asynchronous/background work as Azure Functions (Consumption plan).",
                "Event-driven scaling and pay-per-execution for spiky workloads.", "Could");

        Add("Data", "Store uploaded documents and generated Excel workbooks in Azure Blob Storage (private, no public access).",
            "Durable artefact storage with managed-identity access.", "Must");
        Add("Security", "Use system-assigned managed identity and Azure Key Vault for all secrets; disable local/key auth where supported.",
            "Eliminates secrets in config and enforces keyless, least-privilege access.", "Must");
        Add("Networking", "Enforce HTTPS-only and platform-managed TLS; restrict storage to private access.",
            "Baseline transport security and data exposure reduction.", "Must");
        Add("Observability", "Wire Application Insights + Log Analytics for traces, metrics, and agent run telemetry.",
            "End-to-end diagnostics and cost/usage visibility.", "Should");
        Add("Security", $"Classify data as '{s.DescribeDataSensitivity()}' and apply matching access controls and retention.",
            "Compliance alignment proportional to data sensitivity.", "Should");

        return reqs;
    }

    // ---------------------------------------------------------------- Cost

    private static CostEstimate BuildCost(WorkloadSignals s)
    {
        var est = new CostEstimate
        {
            Currency = AzurePricingCatalog.Currency,
            Region = AzurePricingCatalog.Region,
            ContingencyPercent = s.ContingencyPercent,
            Notes =
            {
                "Reference list prices (Pay-As-You-Go), Australia East, USD. NOT a binding quote.",
                "Replace AzurePricingCatalog with the Azure Retail Prices API for production accuracy.",
                "Each line item links to its first-party Azure pricing page for audit (shown in UI and Excel).",
                "Non-prod view models a scaled-down dev/test footprint of the same architecture; Total = Non-prod + Prod.",
                $"Estimate scale assumptions: {s.DescribeScale()}."
            }
        };

        CostLineItem Item(string svc, string sku, string meter, string assumption, decimal qty, decimal unit, string unitLabel, string cat)
        {
            var reference = AzurePricingCatalog.ResolvePricingReference(svc);
            var li = new CostLineItem
            {
                Service = svc, Sku = sku, Meter = meter, Assumption = assumption,
                Quantity = qty, UnitPrice = unit, Unit = unitLabel, Category = cat,
                MonthlyCost = Math.Round(qty * unit, 2),
                NonProdQuantity = Math.Round(qty * AzurePricingCatalog.NonProdFactor(cat), 4),
                PricingReferenceUrl = reference.Url,
                PricingReferenceLabel = reference.Label
            };
            est.LineItems.Add(li);
            return li;
        }

        // Compute: App Service
        if (s.HasWebApp)
        {
            var sku = s.RecommendedAppServiceSku;
            var price = AzurePricingCatalog.AppServicePlanMonthly.TryGetValue(sku, out var p) ? p : 129.94m;
            Item("Azure App Service", sku, "instance-month", $"{s.AppServiceInstances} instance(s), 730 hrs/mo",
                s.AppServiceInstances, price, "per instance/mo", "Compute");
        }

        // AI: Foundry model token consumption
        if (s.HasAi)
        {
            var rate = AzurePricingCatalog.GetModelRate(s.PreferredModel);
            // Estimate monthly tokens from request volume.
            decimal inputThousands = s.MonthlyAiRequests * s.AvgInputTokens / 1000m;
            decimal outputThousands = s.MonthlyAiRequests * s.AvgOutputTokens / 1000m;
            Item("Microsoft Foundry (Azure OpenAI)", s.PreferredModel, "1K input tokens",
                $"{s.MonthlyAiRequests:N0} req/mo × {s.AvgInputTokens} input tokens",
                Math.Round(inputThousands, 1), rate.InputPer1K, "per 1K tokens", "AI");
            Item("Microsoft Foundry (Azure OpenAI)", s.PreferredModel, "1K output tokens",
                $"{s.MonthlyAiRequests:N0} req/mo × {s.AvgOutputTokens} output tokens",
                Math.Round(outputThousands, 1), rate.OutputPer1K, "per 1K tokens", "AI");
        }

        // AI: file search index
        if (s.HasFileSearch)
        {
            Item("Azure AI Search", "Basic", "service-month", "1 search service for vector/file search",
                1, AzurePricingCatalog.AiSearchBasicMonthly, "per service/mo", "AI");
        }

        // Data
        if (s.HasRelationalDb)
        {
            Item("Azure SQL Database", "GP Serverless 2 vCore", "vCore-month", "Auto-pause enabled; ~average 2 vCore",
                2, AzurePricingCatalog.SqlGpServerlessVCoreMonth, "per vCore/mo", "Data");
        }
        if (s.HasNoSql)
        {
            Item("Azure Cosmos DB", "Serverless", "million RU", $"{s.MonthlyCosmosMillionRu:N0}M RU/mo consumption",
                s.MonthlyCosmosMillionRu, AzurePricingCatalog.CosmosServerlessPerMillionRu, "per 1M RU", "Data");
        }

        // Functions
        if (s.HasFunctions)
        {
            Item("Azure Functions", "Consumption", "million executions", $"{s.MonthlyFunctionMillions:N1}M executions/mo",
                s.MonthlyFunctionMillions, AzurePricingCatalog.FunctionsPerMillionExec, "per 1M exec", "Compute");
        }

        // Storage (always)
        Item("Azure Blob Storage", "Hot LRS", "GB-month", $"{s.StorageGb} GB documents + workbooks",
            s.StorageGb, AzurePricingCatalog.BlobHotPerGbMonth, "per GB/mo", "Data");

        // Observability (always)
        Item("Log Analytics / App Insights", "Pay-as-you-go", "GB ingested", $"{s.LogGbPerMonth} GB telemetry/mo",
            s.LogGbPerMonth, AzurePricingCatalog.LogAnalyticsPerGb, "per GB", "Observability");

        // Security (always)
        Item("Azure Key Vault", "Standard", "10K operations", "Secret reads via managed identity",
            s.KeyVaultOps10K, AzurePricingCatalog.KeyVaultPer10KOps, "per 10K ops", "Security");

        // Networking (always)
        Item("Bandwidth (egress)", "Standard", "GB", $"{s.EgressGb} GB outbound/mo",
            s.EgressGb, AzurePricingCatalog.EgressPerGb, "per GB", "Networking");

        return est;
    }

    // ---------------------------------------------------------------- Project (build) cost

    /// <summary>
    /// Builds the one-time delivery cost: a team of roles with reference day rates and estimated effort.
    /// Roles and effort scale with the workload complexity (scale band + detected capabilities), so a
    /// small POC is a few weeks of effort while an enterprise build is materially larger.
    /// </summary>
    private static ProjectBuildCost BuildProjectCost(WorkloadSignals s)
    {
        // Effort multiplier by scale band (1 = POC, 2 = medium, 3 = enterprise).
        decimal effort = s.ScaleBand switch { >= 3 => 3.0m, 2 => 1.8m, _ => 1.0m };
        decimal Days(decimal baseDays) => Math.Round(baseDays * effort, 1);

        var est = new ProjectBuildCost
        {
            Currency = AzurePricingCatalog.Currency,
            ContingencyPercent = s.ScaleBand >= 3 ? 20m : 15m,
            Notes =
            {
                "One-time cost to design and build the solution (delivery team effort), separate from Azure run cost.",
                "Reference USD day rates and person-day estimates; edit rates/days to re-plan the team.",
                $"Effort scaled to workload complexity: {s.DescribeScale()}.",
                "Assumes a single delivery iteration; excludes ongoing run/support (see Operation Cost)."
            }
        };

        void Role(string role, string desc, decimal dayRate, decimal days) =>
            est.Roles.Add(new ProjectRoleLineItem { Role = role, Description = desc, DayRate = dayRate, EstimatedDays = days });

        // Core roles — always present.
        Role("Solution Architect", "Solution design, Azure architecture, and technical governance.", 600m, Days(10));
        Role("Project Manager", "Delivery planning, coordination, reporting, and stakeholder management.", 500m, Days(14));
        Role("Business Analyst", "Requirements elaboration, acceptance criteria, and backlog grooming.", 420m, Days(8));

        // Build roles — conditional on detected capabilities.
        Role("Backend Developer", "APIs, services, integration, and Azure resource wiring.", 450m, Days(s.HasApi ? 28 : 22));
        if (s.HasWebApp)
            Role("Frontend Developer", "Web UI, client experience, and accessibility.", 420m, Days(20));
        if (s.HasAi)
            Role("AI/ML Engineer", "Foundry agent design, prompts, grounding, and evaluation.", 550m, Days(16));
        if (s.HasRelationalDb || s.HasNoSql)
            Role("Data Engineer", "Data model, storage, ingestion, and query performance.", 480m, Days(12));

        // Quality + delivery enablement — always present.
        Role("QA Engineer", "Test planning, automated tests, and release verification.", 380m, Days(16));
        Role("DevOps Engineer", "CI/CD pipelines, IaC, environments, and observability wiring.", 480m, Days(10));

        return est;
    }

    // ---------------------------------------------------------------- Operation (run) cost

    /// <summary>
    /// Builds the ongoing run/maintain cost after go-live: support, monitoring, patching, enhancements,
    /// plus compliance and AI-ops effort where relevant. Each line is a monthly quantity (hours/mo)
    /// priced at a reference hourly rate; effort scales with workload complexity.
    /// </summary>
    private static OperationCost BuildOperations(WorkloadSignals s)
    {
        decimal effort = s.ScaleBand switch { >= 3 => 2.5m, 2 => 1.6m, _ => 1.0m };
        decimal Hrs(decimal baseHours) => Math.Round(baseHours * effort, 1);

        var est = new OperationCost
        {
            Currency = AzurePricingCatalog.Currency,
            ContingencyPercent = s.ScaleBand >= 3 ? 20m : 15m,
            Notes =
            {
                "Ongoing monthly cost to run, support, and maintain the solution after go-live (excludes Azure infra).",
                "Each line is hours/month × a reference hourly rate; edit quantities/rates to adjust the operating model.",
                $"Effort scaled to workload complexity: {s.DescribeScale()}.",
                "Assumes business-hours support; 24×7 or dedicated managed operations would increase these figures."
            }
        };

        void Op(string item, string desc, string cat, decimal hours, decimal rate) =>
            est.Items.Add(new OperationCostLineItem
            {
                Item = item, Description = desc, Category = cat, Cadence = "Monthly",
                Quantity = hours, UnitPrice = rate, Unit = "per hour"
            });

        // Baseline operating activities — always present.
        Op("Application support (L2/L3)", "Triage, bug fixes, and user support requests.", "Support", Hrs(16), 120m);
        Op("Monitoring & incident response", "Health monitoring, alerts, and incident handling.", "Operations", Hrs(8), 130m);
        Op("Software updates & patching", "Dependency updates, Azure/runtime patching, and security fixes.", "Maintenance", Hrs(6), 120m);
        Op("Minor enhancements & change requests", "Small feature changes and continuous improvement.", "Maintenance", Hrs(10), 130m);

        // Conditional operating activities.
        if (s.MentionsPii || s.MentionsRegulated)
            Op("Security & compliance reviews", "Access reviews, audit evidence, and compliance checks.", "Operations", Hrs(4), 150m);
        if (s.HasAi)
            Op("AI model monitoring & prompt tuning", "Prompt/eval maintenance, quality monitoring, and drift checks.", "Operations", Hrs(6), 140m);

        return est;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"(?im)^\s*project\s*[:\-]\s*(.+)$")]
    private static partial Regex ProjectLineRegex();

    [GeneratedRegex(@"(?m)^\s*#\s+(.+)$")]
    private static partial Regex HeadingRegex();
}
