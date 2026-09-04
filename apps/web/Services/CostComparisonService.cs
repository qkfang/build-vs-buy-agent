using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services.Agents;

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
    private readonly CompareAgent _compareAgent;
    private readonly ILogger<CostComparisonService> _logger;

    public CostComparisonService(
        FoundryOptions options,
        CompareAgent compareAgent,
        ILogger<CostComparisonService> logger)
    {
        _options = options;
        _compareAgent = compareAgent;
        _logger = logger;
    }

    public async Task<CostComparison> CompareAsync(EstimationResult job, CancellationToken ct = default)
    {
        // 1) Deterministic core: parse the "buy" baseline and roll up the "build" estimate.
        var comparison = BuildDeterministicComparison(job);

        // 2) Narrative: prefer the live Compare agent; otherwise fall back to a deterministic narrative.
        if (_options.IsConfigured)
        {
            try
            {
                await _compareAgent.RunAsync(job, comparison, ct);
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

        // ----- BUY side (prefer structured Buy-tab data; fall back to parsing the source documents) -----
        var buy = BuildBuyBaseline(job);
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

    /// <summary>
    /// Prefers the structured Buy tab data (Purchase + Operation Cost steps) when present; otherwise
    /// falls back to regex-parsing a "buy" cost table out of the raw uploaded document text.
    /// </summary>
    private static BuyBaseline BuildBuyBaseline(EstimationResult job)
    {
        var hasStructuredData = (job.Purchase?.Items.Count ?? 0) > 0 || (job.BuyOperations?.Items.Count ?? 0) > 0;
        if (!hasStructuredData)
            return ParseBuyBaseline(job);

        var oneTime = new List<BuyLine>();
        var recurring = new List<BuyLine>();

        foreach (var item in job.Purchase?.Items ?? [])
        {
            var line = new BuyLine(item.Category, item.Cadence, Money2(item.Cost));
            if (string.Equals(item.Cadence, "One-time", StringComparison.OrdinalIgnoreCase))
                oneTime.Add(line);
            else
                recurring.Add(line);
        }

        decimal licensing = 0m, support = 0m;
        foreach (var r in recurring)
        {
            var annual = string.Equals(r.Type, "Monthly", StringComparison.OrdinalIgnoreCase) ? r.Cost * 12m : r.Cost;
            if (r.Category.Contains("licen", StringComparison.OrdinalIgnoreCase)
                || r.Category.Contains("subscription", StringComparison.OrdinalIgnoreCase))
                licensing += annual;
            else
                support += annual;
        }

        if (job.BuyOperations is { Items.Count: > 0 } buyOps)
        {
            foreach (var item in buyOps.Items)
            {
                var annual = item.MonthlyCost * 12m;
                recurring.Add(new BuyLine(item.Category, item.Cadence, annual));
                support += annual;
            }
        }

        return new BuyBaseline(
            true,
            Math.Round(oneTime.Sum(l => l.Cost), 2),
            Math.Round(licensing, 2),
            Math.Round(support, 2),
            oneTime,
            recurring);
    }

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

    private static string Money(decimal amount) =>
        "$" + amount.ToString("N2", CultureInfo.InvariantCulture);

    // Matches a markdown table row with at least 3 pipe-delimited cells: | category | type | $cost | ...
    [GeneratedRegex(@"^\|\s*(?<cat>[^|]*?)\s*\|\s*(?<type>[^|]*?)\s*\|\s*(?<cost>[^|]*?)\s*\|", RegexOptions.Multiline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"\$?\s*([\d][\d,]*(?:\.\d+)?)")]
    private static partial Regex MoneyRegex();
}
