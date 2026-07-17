using Xunit;
using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services;

namespace Proj37.CostEstimator.Tests;

public class OfflineEstimationEngineTests
{
    private static EstimationResult JobFrom(string text, string file = "brief.md")
    {
        var job = new EstimationResult();
        job.Documents.Add(new IngestedDocument { FileName = file, ExtractedText = text, WordCount = text.Split(' ').Length });
        return job;
    }

    [Fact]
    public async Task Estimate_produces_scope_requirements_and_costs()
    {
        var text = """
            Project: Contoso AI Intake
            Build a web app with an API that uses an AI/LLM (Foundry) prompt agent to ingest documents
            (PDF, DOCX) for RAG/file search. Store data in SQL and Cosmos DB. This is for an enterprise
            with high throughput, millions of requests, and contains PII subject to compliance.
            """;
        var engine = new OfflineEstimationEngine();
        var job = await engine.EstimateAsync(JobFrom(text));

        Assert.Equal("completed", job.Status);
        Assert.Equal("offline", job.Engine);
        Assert.False(string.IsNullOrWhiteSpace(job.Scope.ProjectName));
        Assert.Contains("Contoso AI Intake", job.Scope.ProjectName);
        Assert.NotEmpty(job.Requirements);
        Assert.NotEmpty(job.Cost.LineItems);
        Assert.True(job.Cost.MonthlyTotal > 0);
        Assert.Equal(5, job.AgentSteps.Count); // scope, requirements, cost, project, operations
    }

    [Fact]
    public async Task Detects_ai_and_filesearch_signals()
    {
        var job = await new OfflineEstimationEngine().EstimateAsync(
            JobFrom("An AI agent with Foundry that does document file search and RAG grounding."));

        // AI workload should pull in Foundry token line items and an AI Search line item.
        Assert.Contains(job.Cost.LineItems, l => l.Category == "AI");
        Assert.Contains(job.Requirements, r => r.Category == "AI");
    }

    [Fact]
    public async Task Enterprise_scale_costs_more_than_poc_scale()
    {
        var poc = await new OfflineEstimationEngine().EstimateAsync(
            JobFrom("Small POC web app prototype demo for a single team."));
        var ent = await new OfflineEstimationEngine().EstimateAsync(
            JobFrom("Enterprise mission critical web app, global, millions of users, high throughput at scale."));

        Assert.True(ent.Cost.MonthlyTotalWithContingency > poc.Cost.MonthlyTotalWithContingency,
            $"enterprise {ent.Cost.MonthlyTotalWithContingency} should exceed poc {poc.Cost.MonthlyTotalWithContingency}");
    }

    [Fact]
    public async Task Always_includes_baseline_security_and_observability()
    {
        var job = await new OfflineEstimationEngine().EstimateAsync(JobFrom("Basic internal web app."));
        Assert.Contains(job.Cost.LineItems, l => l.Category == "Security");
        Assert.Contains(job.Cost.LineItems, l => l.Category == "Observability");
        Assert.Contains(job.Cost.LineItems, l => l.Service.Contains("Blob"));
    }

    [Fact]
    public async Task Produces_project_build_cost_with_core_roles()
    {
        var job = await new OfflineEstimationEngine().EstimateAsync(JobFrom("Internal web app with an API."));
        Assert.NotEmpty(job.ProjectCost.Roles);
        Assert.Contains(job.ProjectCost.Roles, r => r.Role.Contains("Architect"));
        Assert.Contains(job.ProjectCost.Roles, r => r.Role.Contains("QA"));
        Assert.Contains(job.ProjectCost.Roles, r => r.Role.Contains("Project Manager"));
        Assert.All(job.ProjectCost.Roles, r => Assert.Equal(Math.Round(r.DayRate * r.EstimatedDays, 2), r.Cost));
        Assert.True(job.ProjectCost.TotalWithContingency > job.ProjectCost.LaborTotal);
    }

    [Fact]
    public async Task Produces_operation_cost_with_support_and_maintenance()
    {
        var job = await new OfflineEstimationEngine().EstimateAsync(JobFrom("Internal web app with an API."));
        Assert.NotEmpty(job.Operations.Items);
        Assert.Contains(job.Operations.Items, i => i.Category == "Support");
        Assert.Contains(job.Operations.Items, i => i.Category == "Maintenance");
        Assert.All(job.Operations.Items, i => Assert.Equal(Math.Round(i.Quantity * i.UnitPrice, 2), i.MonthlyCost));
        Assert.True(job.Operations.MonthlyTotalWithContingency > job.Operations.MonthlyTotal);
    }

    [Fact]
    public async Task Ai_workload_adds_ai_ops_line_and_ml_role()
    {
        var job = await new OfflineEstimationEngine().EstimateAsync(
            JobFrom("An AI agent with Foundry that does document file search and RAG grounding."));
        Assert.Contains(job.ProjectCost.Roles, r => r.Role.Contains("AI/ML"));
        Assert.Contains(job.Operations.Items, i => i.Item.Contains("AI model"));
    }

    [Fact]
    public async Task Enterprise_build_effort_exceeds_poc_build_effort()
    {
        var poc = await new OfflineEstimationEngine().EstimateAsync(
            JobFrom("Small POC web app prototype demo for a single team."));
        var ent = await new OfflineEstimationEngine().EstimateAsync(
            JobFrom("Enterprise mission critical web app, global, millions of users, high throughput at scale."));
        Assert.True(ent.ProjectCost.TotalDays > poc.ProjectCost.TotalDays,
            $"enterprise days {ent.ProjectCost.TotalDays} should exceed poc days {poc.ProjectCost.TotalDays}");
    }

    [Fact]
    public async Task Contingency_total_is_greater_than_raw_total()
    {
        var job = await new OfflineEstimationEngine().EstimateAsync(JobFrom("Web app with API."));
        Assert.True(job.Cost.MonthlyTotalWithContingency > job.Cost.MonthlyTotal);
        Assert.Equal(Math.Round(job.Cost.MonthlyTotal * 12m, 2), job.Cost.AnnualTotal);
    }

    [Fact]
    public async Task Project_name_comes_from_heading_not_filename()
    {
        // The engine injects "# <fileName>" into the corpus; the project name must skip that and
        // use the document's own "# Project: ..." heading.
        var text = "# Project: Wingtip Retail Analytics API\n\nA back-end analytics API for stores.";
        var job = await new OfflineEstimationEngine().EstimateAsync(
            JobFrom(text, file: "03-wingtip-retail-analytics-api.md"));
        Assert.Equal("Wingtip Retail Analytics API", job.Scope.ProjectName);
        Assert.DoesNotContain(".md", job.Scope.ProjectName);
    }

    [Fact]
    public async Task Storage_keyword_does_not_falsely_trigger_ai()
    {
        // "storage" contains the substring "rag" — it must NOT be detected as an AI/RAG workload.
        var job = await new OfflineEstimationEngine().EstimateAsync(
            JobFrom("A data and integration API. Blob storage for archives. Relational SQL aggregates. No language features."));
        Assert.DoesNotContain(job.Cost.LineItems, l => l.Service.Contains("Foundry"));
        Assert.DoesNotContain(job.Cost.LineItems, l => l.Service.Contains("AI Search"));
        Assert.DoesNotContain(job.Requirements, r => r.Category == "AI");
    }
}
