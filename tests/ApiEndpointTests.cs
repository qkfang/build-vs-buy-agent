using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Proj37.CostEstimator.Tests;

/// <summary>
/// End-to-end API tests via WebApplicationFactory. Foundry is unconfigured in test, so the
/// deterministic offline engine runs — exercising the full ingest -> estimate -> Excel pipeline.
/// </summary>
public class ApiEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_ok_and_offline_engine()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("healthy", body);
        Assert.Contains("offline", body); // no Foundry config in tests
    }

    [Fact]
    public async Task Sample_estimation_runs_and_workbook_downloads()
    {
        var client = _factory.CreateClient();

        var run = await client.PostAsync("/api/estimations/sample", content: null);
        run.EnsureSuccessStatusCode();
        var job = await run.Content.ReadFromJsonAsync<JobDto>();
        Assert.NotNull(job);
        Assert.Equal("completed", job!.status);
        Assert.False(string.IsNullOrWhiteSpace(job.jobId));
        Assert.True(job.cost!.lineItems!.Count > 0);

        // Download the generated workbook.
        var wb = await client.GetAsync($"/api/estimations/{job.jobId}/workbook");
        wb.EnsureSuccessStatusCode();
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            wb.Content.Headers.ContentType?.MediaType);
        var bytes = await wb.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 2000);

        // It should now appear in the list.
        var list = await client.GetFromJsonAsync<List<ListDto>>("/api/estimations");
        Assert.NotNull(list);
        Assert.Contains(list!, x => x.jobId == job.jobId);
    }

    [Fact]
    public async Task Upload_estimation_via_multipart_works()
    {
        var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        var doc = new StringContent("Project: Upload Test\nA web app with API and an AI Foundry agent for document file search.",
            Encoding.UTF8, "text/markdown");
        form.Add(doc, "files", "upload-brief.md");

        var run = await client.PostAsync("/api/estimations", form);
        run.EnsureSuccessStatusCode();
        var job = await run.Content.ReadFromJsonAsync<JobDto>();
        Assert.Equal("completed", job!.status);
        Assert.Contains("Upload Test", job.scope!.projectName);
    }

    [Fact]
    public async Task Missing_job_returns_404()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/estimations/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Samples_list_returns_bundled_requirement_docs()
    {
        var client = _factory.CreateClient();
        var items = await client.GetFromJsonAsync<List<SampleDto>>("/api/samples");
        Assert.NotNull(items);
        Assert.True(items!.Count >= 4, "expected at least 4 bundled sample docs");
        Assert.All(items!, s => Assert.False(string.IsNullOrWhiteSpace(s.id)));
        Assert.All(items!, s => Assert.False(string.IsNullOrWhiteSpace(s.title)));
        // Titles must not leak the raw filename (friendly heading is used).
        Assert.All(items!, s => Assert.DoesNotContain(".md", s.title));
        // No "claims" terminology anywhere in the sample catalogue.
        Assert.DoesNotContain(items!, s => s.title.Contains("claim", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sample_content_returns_markdown_and_is_claims_free()
    {
        var client = _factory.CreateClient();
        var items = await client.GetFromJsonAsync<List<SampleDto>>("/api/samples");
        Assert.NotNull(items);
        foreach (var s in items!)
        {
            var resp = await client.GetAsync($"/api/samples/{s.id}");
            resp.EnsureSuccessStatusCode();
            var md = await resp.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(md));
            Assert.DoesNotContain("claim", md, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Sample_content_rejects_path_traversal()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/samples/..%2f..%2fappsettings");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Sample_estimation_by_id_runs_named_project()
    {
        var client = _factory.CreateClient();
        var run = await client.PostAsync("/api/estimations/sample?id=03-wingtip-retail-analytics-api", content: null);
        run.EnsureSuccessStatusCode();
        var job = await run.Content.ReadFromJsonAsync<JobDto>();
        Assert.Equal("completed", job!.status);
        // Project name comes from the document heading, not the file name.
        Assert.Equal("Wingtip Retail Analytics API", job.scope!.projectName);
        // This is a deliberately AI-free brief: no Foundry/AI line items.
        Assert.DoesNotContain(job.cost!.lineItems!, l => l.service.Contains("Foundry"));
    }

    [Fact]
    public async Task Agent_instructions_expose_three_steps()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/agent-instructions");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("scope", body);
        Assert.Contains("requirements", body);
        Assert.Contains("features", body);
        Assert.Contains("cost", body);
        Assert.Contains("spec", body);
        Assert.Contains("purchase", body);
        Assert.Contains("buyoperations", body);
        Assert.Contains("persona", body);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/platform/scope")]
    [InlineData("/platform/requirements")]
    [InlineData("/platform/features")]
    [InlineData("/platform/cost")]
    [InlineData("/platform/steps")]
    [InlineData("/platform/spec")]
    [InlineData("/platform/purchase")]
    [InlineData("/platform/buy-operations")]
    [InlineData("/estimations")]
    public async Task Physical_pages_render(string url)
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Build vs Buy Agent", html);
        Assert.Contains("mainnav", html); // top nav present on every page
    }

    // Minimal DTOs mirroring the JSON shape we assert on.
    private sealed record JobDto(string jobId, string status, ScopeDto? scope, CostDto? cost);
    private sealed record ScopeDto(string projectName);
    private sealed record CostDto(List<LineDto>? lineItems);
    private sealed record LineDto(string service, decimal monthlyCost);
    private sealed record ListDto(string jobId, string project);
    private sealed record SampleDto(string id, string title, string fileName, int sizeBytes);
}
