using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Produces a Build-vs-Buy cost comparison for an estimation job.
///
/// The numeric analysis is deterministic and auditable:
///   • BUILD side is rolled up from the agentic estimate (one-time delivery cost + Azure infrastructure
///     + ongoing run/support).
///   • BUY side is parsed from the "off-the-shelf / COTS" cost table in the source documents.
/// A Compare (Build-vs-Buy Analyst) agent then enriches the numbers with a narrative summary, a
/// recommendation, and per-section reasoning. When Foundry is not configured (or the call fails), a
/// deterministic offline narrative is generated instead, so the feature always works.
/// </summary>
public sealed partial class CostComparisonService
{
    private readonly FoundryOptions _options;
    private readonly ILogger<CostComparisonService> _logger;

    public CostComparisonService(FoundryOptions options, ILogger<CostComparisonService> logger)
    {
        _options = options;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<CostComparison> CompareAsync(EstimationResult job, CancellationToken ct = default)
    {
        // 1) Deterministic core: parse the "buy" baseline and roll up the "build" estimate.
        var comparison = BuildDeterministicComparison(job);

        // 2) Narrative: prefer the live Compare agent; otherwise fall back to a deterministic narrative.
        if (_options.IsConfigured)
        {
            try
            {
                await ApplyAgentNarrativeAsync(job, comparison, ct);
                comparison.Engine = "foundry";
                return comparison;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compare agent failed; falling back to offline narrative.");
                comparison.Notes.Add($"Compare agent unavailable ({ex.GetType().Name}); used deterministic reasoning.");
            }
        }

        ApplyOfflineNarrative(comparison);
        comparison.Engine = "offline";
        return comparison;
    }

    // ---------------------------------------------------------------- deterministic core

    private static CostComparison BuildDeterministicComparison(EstimationResult job)
    {
        var cmp = new CostComparison
        {
            JobId = job.JobId,
            Notes =
            {
                "Buy costs are read from the off-the-shelf cost section of the source documents.",
                "All figures are reference estimates for comparison only — not a binding quote."
            }
        };

        // ----- BUILD side -----
        var buildOneTime = Money2(job.ProjectCost.TotalWithContingency);              // one-time delivery/build
        var buildAzureAnnual = Money2(job.Cost.MonthlyTotalWithContingency * 12m);    // production Azure infra / yr
        var buildOpsAnnual = Money2(job.Operations.AnnualTotalWithContingency);       // run & support / yr

        // ----- BUY side (parsed from documents) -----
        var buy = ParseBuyBaseline(job);
        cmp.BuyCostAvailable = buy.Found;

        var buyOneTime = buy.OneTimeTotal;
        var buyLicensingAnnual = buy.LicensingAnnual;
        var buySupportAnnual = buy.SupportAnnual;

        // ----- Sections -----
        cmp.Sections.Add(new CostComparisonSection
        {
            Section = "Solution setup & build (one-time)",
            CostType = "One-time",
            BuildCost = buildOneTime,
            BuildDetail = $"Delivery team build ({job.ProjectCost.Roles.Count} roles, ~{job.ProjectCost.TotalDays:N0} person-days, incl. {job.ProjectCost.ContingencyPercent:N0}% contingency).",
            BuyCost = buyOneTime,
            BuyDetail = buy.Found
                ? $"One-time buy items (onboarding, setup, migration, integration, accreditation, training): {buy.OneTimeItems.Count} line(s)."
                : "No off-the-shelf one-time cost found in the source documents."
        });

        cmp.Sections.Add(new CostComparisonSection
        {
            Section = "Cloud infrastructure & licensing (annual)",
            CostType = "Annual (recurring)",
            BuildCost = buildAzureAnnual,
            BuildDetail = $"Azure infrastructure for production: {Money(job.Cost.MonthlyTotalWithContingency)}/mo × 12 (incl. {job.Cost.ContingencyPercent:N0}% contingency).",
            BuyCost = buyLicensingAnnual,
            BuyDetail = buy.Found
                ? "Vendor product licensing / subscription (recurring)."
                : "No off-the-shelf licensing cost found in the source documents."
        });

        cmp.Sections.Add(new CostComparisonSection
        {
            Section = "Run, support & maintenance (annual)",
            CostType = "Annual (recurring)",
            BuildCost = buildOpsAnnual,
            BuildDetail = $"Ongoing run/support of the built solution: {job.Operations.Items.Count} operating line(s) (incl. {job.Operations.ContingencyPercent:N0}% contingency).",
            BuyCost = buySupportAnnual,
            BuyDetail = buy.Found
                ? "Vendor support & maintenance + premium SLA uplift (recurring)."
                : "No off-the-shelf support cost found in the source documents."
        });

        // ----- Totals -----
        cmp.Totals = new ComparisonTotals
        {
            BuildOneTime = buildOneTime,
            BuildAnnualRecurring = Math.Round(buildAzureAnnual + buildOpsAnnual, 2),
            BuyOneTime = buyOneTime,
            BuyAnnualRecurring = Math.Round(buyLicensingAnnual + buySupportAnnual, 2)
        };

        return cmp;
    }

    private static decimal Money2(decimal amount) => Math.Round(amount, 2);

    // ---------------------------------------------------------------- buy-baseline parser

    private sealed record BuyBaseline(
        bool Found,
        decimal OneTimeTotal,
        decimal LicensingAnnual,
        decimal SupportAnnual,
        List<BuyLine> OneTimeItems,
        List<BuyLine> RecurringItems);

    private sealed record BuyLine(string Category, string Type, decimal Cost);

    /// <summary>
    /// Extracts the off-the-shelf / COTS "buy" cost table from the ingested document text. Recognises a
    /// markdown table whose rows are "| category | type | $amount | notes |" and classifies each row as a
    /// one-time or recurring cost, splitting recurring costs into licensing vs support/run buckets.
    /// </summary>
    private static BuyBaseline ParseBuyBaseline(EstimationResult job)
    {
        var text = string.Join("\n\n", job.Documents.Select(d =>
            string.IsNullOrWhiteSpace(d.ExtractedText) ? (d.Excerpt ?? "") : d.ExtractedText));

        var oneTime = new List<BuyLine>();
        var recurring = new List<BuyLine>();

        foreach (Match row in TableRowRegex().Matches(text))
        {
            var category = CleanCell(row.Groups["cat"].Value);
            var type = CleanCell(row.Groups["type"].Value);
            var costCell = row.Groups["cost"].Value;

            if (category.Length == 0) continue;
            // Skip header and any roll-up "total" rows so we don't double-count.
            if (category.StartsWith("cost category", StringComparison.OrdinalIgnoreCase)) continue;
            if (category.Contains("total", StringComparison.OrdinalIgnoreCase)) continue;
            if (category.Contains("ongoing annual run cost", StringComparison.OrdinalIgnoreCase)) continue;

            var amount = ParseMoney(costCell);
            if (amount <= 0) continue;

            var isRecurring = type.Contains("recurring", StringComparison.OrdinalIgnoreCase)
                || type.Contains("annual", StringComparison.OrdinalIgnoreCase)
                || costCell.Contains("/ yr", StringComparison.OrdinalIgnoreCase)
                || costCell.Contains("/yr", StringComparison.OrdinalIgnoreCase);

            var line = new BuyLine(category, type, amount);
            if (isRecurring) recurring.Add(line); else oneTime.Add(line);
        }

        var found = oneTime.Count > 0 || recurring.Count > 0;

        decimal licensing = 0m, support = 0m;
        foreach (var r in recurring)
        {
            if (r.Category.Contains("licen", StringComparison.OrdinalIgnoreCase)
                || r.Category.Contains("subscription", StringComparison.OrdinalIgnoreCase))
                licensing += r.Cost;
            else
                support += r.Cost;
        }

        return new BuyBaseline(
            found,
            Math.Round(oneTime.Sum(l => l.Cost), 2),
            Math.Round(licensing, 2),
            Math.Round(support, 2),
            oneTime,
            recurring);
    }

    private static string CleanCell(string s) =>
        s.Replace("*", "").Replace("≈", "").Trim();

    private static decimal ParseMoney(string cell)
    {
        var m = MoneyRegex().Match(cell);
        if (!m.Success) return 0m;
        var digits = m.Groups[1].Value.Replace(",", "");
        return decimal.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    // ---------------------------------------------------------------- offline narrative

    private static void ApplyOfflineNarrative(CostComparison c)
    {
        foreach (var s in c.Sections)
        {
            if (!c.BuyCostAvailable)
            {
                s.Reasoning = "No comparable off-the-shelf figure was found in the source documents for this section.";
                continue;
            }
            var diff = Math.Abs(s.Difference);
            s.Reasoning = s.Cheaper switch
            {
                "build" => $"Building is cheaper here by {Money(diff)} — the agentic estimate avoids vendor {(s.CostType == "One-time" ? "onboarding/implementation fees" : "licensing/subscription mark-up")}.",
                "buy" => $"Buying is cheaper here by {Money(diff)} — the vendor absorbs this into a packaged price, undercutting the {(s.CostType == "One-time" ? "bespoke build effort" : "run/support you would carry yourself")}.",
                _ => "The two options are effectively level for this section."
            };
        }

        var t = c.Totals;
        if (!c.BuyCostAvailable)
        {
            c.Recommendation = "neutral";
            c.Summary = "The agentic Azure build cost is available, but the source documents do not contain an off-the-shelf 'buy' cost section to compare against. Add a COTS/SaaS price list to the brief to enable a full Build-vs-Buy recommendation.";
            c.Reasoning.Add($"Build one-time: {Money(t.BuildOneTime)}; build annual run: {Money(t.BuildAnnualRecurring)}.");
            c.Reasoning.Add("No buy baseline detected in the documents, so no comparison could be made.");
            return;
        }

        var buildTco = t.BuildThreeYearTco;
        var buyTco = t.BuyThreeYearTco;
        var lower = Math.Min(buildTco, buyTco);
        var withinTenPct = lower > 0 && Math.Abs(buildTco - buyTco) / lower <= 0.10m;

        c.Recommendation = withinTenPct ? "neutral" : (buildTco < buyTco ? "build" : "buy");
        var gap = Math.Abs(buyTco - buildTco);

        c.Summary = c.Recommendation switch
        {
            "build" => $"Building on Azure is the more cost-effective option over 3 years: {Money(buildTco)} vs {Money(buyTco)} to buy — a saving of about {Money(gap)}. The larger up-front build effort is outweighed by materially lower recurring cost.",
            "buy"   => $"Buying the off-the-shelf product is the more cost-effective option over 3 years: {Money(buyTco)} vs {Money(buildTco)} to build — about {Money(gap)} cheaper. The recurring cost advantage of building does not repay the build investment within the horizon.",
            _        => $"Build and Buy are within ~10% on a 3-year TCO ({Money(buildTco)} vs {Money(buyTco)}); the decision should be driven by qualitative factors (control, customisation, lock-in, time-to-value) rather than cost alone."
        };

        c.Reasoning.Add($"Year-1 cost — Build {Money(t.BuildYearOne)} vs Buy {Money(t.BuyYearOne)}.");
        c.Reasoning.Add($"Ongoing annual run cost — Build {Money(t.BuildAnnualRecurring)} vs Buy {Money(t.BuyAnnualRecurring)}.");
        c.Reasoning.Add($"One-time cost — Build {Money(t.BuildOneTime)} vs Buy {Money(t.BuyOneTime)}.");
        c.Reasoning.Add($"3-year total cost of ownership — Build {Money(buildTco)} vs Buy {Money(buyTco)}.");
        c.Reasoning.Add("Non-cost factors to weigh: building maximises control and customisation; buying accelerates time-to-value but adds vendor lock-in and per-seat/volume price growth.");
    }

    // ---------------------------------------------------------------- agent narrative

    private async Task ApplyAgentNarrativeAsync(EstimationResult job, CostComparison c, CancellationToken ct)
    {
        var agent = CreateAgent();
        var prompt = ComparePrompt(job, c);
        var response = await agent.RunAsync(prompt, cancellationToken: ct);
        var json = ExtractJsonObject(response.Text ?? "");
        var narrative = string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<AgentNarrative>(json!, JsonOpts);

        if (narrative is null)
        {
            ApplyOfflineNarrative(c);
            c.Notes.Add("Compare agent returned no parseable narrative; used deterministic reasoning.");
            return;
        }

        c.Summary = string.IsNullOrWhiteSpace(narrative.Summary) ? c.Summary : narrative.Summary.Trim();
        if (!string.IsNullOrWhiteSpace(narrative.Recommendation))
            c.Recommendation = narrative.Recommendation.Trim().ToLowerInvariant() switch
            {
                "build" or "buy" or "neutral" => narrative.Recommendation.Trim().ToLowerInvariant(),
                _ => c.Recommendation
            };

        if (narrative.Reasoning is { Count: > 0 }) c.Reasoning = narrative.Reasoning;

        foreach (var s in c.Sections)
        {
            if (narrative.SectionReasoning is not null &&
                narrative.SectionReasoning.TryGetValue(s.Section, out var r) && !string.IsNullOrWhiteSpace(r))
            {
                s.Reasoning = r.Trim();
            }
        }

        // Backfill any sections the agent skipped with a deterministic line so the UI is never blank.
        foreach (var s in c.Sections.Where(s => string.IsNullOrWhiteSpace(s.Reasoning)))
        {
            var diff = Math.Abs(s.Difference);
            s.Reasoning = s.Cheaper == "n/a"
                ? "No comparable figure available for this section."
                : $"{(s.Cheaper == "build" ? "Build" : "Buy")} is cheaper here by {Money(diff)}.";
        }
    }

    private AIAgent CreateAgent()
    {
        var credential = string.IsNullOrWhiteSpace(_options.TenantId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = _options.TenantId });
        var client = new AIProjectClient(new Uri(_options.ProjectEndpoint!), credential);
        return client.AsAIAgent(
            model: _options.ModelDeploymentName,
            instructions: AgentInstructions.SystemPersona + "\n\n" + AgentInstructions.Compare.Instructions,
            name: _options.AgentName + "-compare");
    }

    private static string ComparePrompt(EstimationResult job, CostComparison c)
    {
        var structured = new
        {
            buyCostAvailable = c.BuyCostAvailable,
            totals = c.Totals,
            sections = c.Sections.Select(s => new
            {
                s.Section, s.CostType, s.BuildCost, s.BuildDetail, s.BuyCost, s.BuyDetail, s.Difference, s.Cheaper
            })
        };

        var corpus = string.Join("\n\n", job.Documents.Select(d =>
            $"=== FILE: {d.FileName} ===\n{(string.IsNullOrWhiteSpace(d.ExtractedText) ? d.Excerpt : d.ExtractedText)}"));
        if (corpus.Length > 24_000) corpus = corpus[..24_000] + "…";

        return $$"""
        Compare BUILDING this solution on Azure against BUYING an off-the-shelf product.
        The application has already computed the numbers below; do NOT recompute them.
        Explain and recommend based strictly on these figures and the source cost section.

        STRUCTURED COMPARISON:
        {{JsonSerializer.Serialize(structured, JsonOpts)}}

        Return ONLY this JSON object:
        {
          "summary": string,
          "recommendation": "build" | "buy" | "neutral",
          "sectionReasoning": { "<exact section name>": string, ... },
          "reasoning": string[]
        }

        Use the EXACT section names from the structured comparison as the keys of sectionReasoning.
        Choose "neutral" only when Build and Buy are within ~10% on 3-year TCO.

        SOURCE DOCUMENTS (for context on what the buy price covers):
        {{corpus}}
        """;
    }

    private static string? ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text.Substring(start, end - start + 1);
    }

    private static string Money(decimal amount) =>
        "$" + amount.ToString("N2", CultureInfo.InvariantCulture);

    private sealed class AgentNarrative
    {
        [JsonPropertyName("summary")] public string Summary { get; set; } = "";
        [JsonPropertyName("recommendation")] public string Recommendation { get; set; } = "";
        [JsonPropertyName("sectionReasoning")] public Dictionary<string, string>? SectionReasoning { get; set; }
        [JsonPropertyName("reasoning")] public List<string> Reasoning { get; set; } = new();
    }

    // Matches a markdown table row with at least 3 pipe-delimited cells: | category | type | $cost | ...
    [GeneratedRegex(@"^\|\s*(?<cat>[^|]*?)\s*\|\s*(?<type>[^|]*?)\s*\|\s*(?<cost>[^|]*?)\s*\|", RegexOptions.Multiline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"\$?\s*([\d][\d,]*(?:\.\d+)?)")]
    private static partial Regex MoneyRegex();
}
