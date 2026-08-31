using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Proj37.CostEstimator.Tests;

/// <summary>
/// Session API integration tests. These cover the upload-first session flow where each agent-backed
/// step is run independently and persisted to a session directory on disk.
/// </summary>
public sealed class SessionApiTests : IClassFixture<SessionApiTests.SessionWebApplicationFactory>
{
    private static readonly string[] StepKeys = ["scope", "requirements", "cost", "project", "operations", "compare"];
    private readonly SessionWebApplicationFactory _factory;

    public SessionApiTests(SessionWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_session_is_upload_only_and_persists_pending_state()
    {
        var client = _factory.CreateClient();
        var session = await CreateSessionAsync(client);

        Assert.False(string.IsNullOrWhiteSpace(session.sessionId));
        Assert.Single(session.documents);
        Assert.Null(session.scope);
        Assert.Empty(session.requirements);
        Assert.Null(session.cost);
        Assert.Null(session.projectCost);
        Assert.Null(session.operations);
        Assert.Null(session.compare);
        Assert.Empty(session.agentSteps);
        Assert.NotNull(session.steps);
        Assert.All(StepKeys, step => Assert.Equal("pending", session.steps[step].status));

        var list = await client.GetFromJsonAsync<List<SessionSummaryDto>>("/api/sessions");
        Assert.NotNull(list);
        Assert.Contains(list!, s => s.sessionId == session.sessionId);

        var jsonPath = _factory.GetSessionJsonPath(session.sessionId);
        Assert.True(File.Exists(jsonPath), $"expected persisted session file at {jsonPath}");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
        Assert.Equal(session.sessionId, doc.RootElement.GetProperty("sessionId").GetString());
        Assert.True(doc.RootElement.TryGetProperty("steps", out _));
        Assert.True(doc.RootElement.TryGetProperty("documents", out _));
    }

    [Fact]
    public async Task Session_steps_run_individually_can_rerun_and_generate_workbook()
    {
        var client = _factory.CreateClient();
        var session = await CreateSessionAsync(client);

        session = await RunStepAsync(client, session.sessionId, "scope");
        Assert.Equal("completed", session.steps["scope"].status);
        Assert.NotNull(session.scope);
        Assert.Contains("Session API", session.scope!.projectName);

        session = await RunStepAsync(client, session.sessionId, "requirements");
        Assert.Equal("completed", session.steps["requirements"].status);
        Assert.NotEmpty(session.requirements);

        session = await RunStepAsync(client, session.sessionId, "cost");
        Assert.Equal("completed", session.steps["cost"].status);
        Assert.NotNull(session.cost);
        Assert.NotEmpty(session.cost!.lineItems);

        session = await RunStepAsync(client, session.sessionId, "project");
        Assert.Equal("completed", session.steps["project"].status);
        Assert.NotNull(session.projectCost);
        Assert.NotEmpty(session.projectCost!.roles);

        session = await RunStepAsync(client, session.sessionId, "operations");
        Assert.Equal("completed", session.steps["operations"].status);
        Assert.NotNull(session.operations);
        Assert.NotEmpty(session.operations!.items);

        var workbook = await client.GetAsync($"/api/sessions/{session.sessionId}/workbook");
        workbook.EnsureSuccessStatusCode();
        var workbookBytes = await workbook.Content.ReadAsByteArrayAsync();
        Assert.True(workbookBytes.Length > 2000);

        session = await RunStepAsync(client, session.sessionId, "compare");
        Assert.Equal("completed", session.steps["compare"].status);
        Assert.NotNull(session.compare);
        Assert.False(string.IsNullOrWhiteSpace(session.compare!.summary));

        var beforeLastRun = session.steps["scope"].lastRunUtc;
        var beforeLogs = session.agentSteps.Count;
        session = await RunStepAsync(client, session.sessionId, "scope");
        Assert.Equal("completed", session.steps["scope"].status);
        Assert.NotNull(session.steps["scope"].lastRunUtc);
        Assert.True(session.steps["scope"].lastRunUtc >= beforeLastRun);
        Assert.True(session.agentSteps.Count > beforeLogs);
    }

    [Fact]
    public async Task Unknown_step_and_missing_session_return_400_and_404()
    {
        var client = _factory.CreateClient();
        var session = await CreateSessionAsync(client);

        var badStep = await client.PostAsync($"/api/sessions/{session.sessionId}/steps/not-a-step", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, badStep.StatusCode);

        var missing = await client.GetAsync("/api/sessions/session-19990101000000000");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var missingRun = await client.PostAsync("/api/sessions/session-19990101000000000/steps/scope", content: null);
        Assert.Equal(HttpStatusCode.NotFound, missingRun.StatusCode);
    }

    private static async Task<SessionDto> CreateSessionAsync(HttpClient client)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(
            """
            Project: Session API Test
            Build a web app with an API and a Foundry agent for document search. Use SQL storage,
            production deployment, and PII-aware controls.
            """,
            Encoding.UTF8,
            "text/markdown"), "files", "session-brief.md");

        var response = await client.PostAsync("/api/sessions", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionDto>())!;
    }

    private static async Task<SessionDto> RunStepAsync(HttpClient client, string sessionId, string step)
    {
        var response = await client.PostAsync($"/api/sessions/{sessionId}/steps/{step}", content: null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionDto>())!;
    }

    public sealed class SessionWebApplicationFactory : WebApplicationFactory<Program>
    {
        public string DataRoot { get; } = Path.Combine(
            AppContext.BaseDirectory,
            "session-test-data",
            Guid.NewGuid().ToString("N"));

        private static string DefaultAppDataRoot => Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "apps", "web", "App_Data"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Foundry:Enabled"] = "false",
                    ["Storage:LocalDataFolder"] = DataRoot
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(DataRoot))
            {
                Directory.Delete(DataRoot, recursive: true);
            }
        }

        public string GetSessionJsonPath(string sessionId)
        {
            var custom = Path.Combine(DataRoot, sessionId, "session.json");
            if (File.Exists(custom))
                return custom;

            return Path.Combine(DefaultAppDataRoot, sessionId, "session.json");
        }
    }

    private sealed record SessionDto(
        string sessionId,
        List<DocumentDto> documents,
        ScopeDto? scope,
        List<RequirementDto> requirements,
        CostDto? cost,
        ProjectCostDto? projectCost,
        OperationsDto? operations,
        CompareDto? compare,
        List<AgentStepDto> agentSteps,
        Dictionary<string, StepStateDto> steps);

    private sealed record DocumentDto(string fileName);
    private sealed record ScopeDto(string projectName);
    private sealed record RequirementDto(string id);
    private sealed record CostDto(List<LineDto> lineItems);
    private sealed record LineDto(string service);
    private sealed record ProjectCostDto(List<RoleDto> roles);
    private sealed record RoleDto(string role);
    private sealed record OperationsDto(List<OperationItemDto> items);
    private sealed record OperationItemDto(string item);
    private sealed record CompareDto(string summary, string recommendation);
    private sealed record AgentStepDto(string step, string summary);
    private sealed record StepStateDto(string status, DateTimeOffset? lastRunUtc, string? error);
    private sealed record SessionSummaryDto(string sessionId, string? project, string status);
}
