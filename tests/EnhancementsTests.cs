using Xunit;
using ClosedXML.Excel;
using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services;

namespace Proj37.CostEstimator.Tests;

/// <summary>
/// Tests for the 2026-06-21 enhancement round: Markdown rendering of sample docs, per-line Azure pricing
/// reference links (UI + Excel), and the non-prod / prod / total environment cost model (UI + Excel sheets).
/// </summary>
public class EnhancementsTests
{
    private static async Task<EstimationResult> SampleJobAsync()
    {
        var job = new EstimationResult();
        job.Documents.Add(new IngestedDocument
        {
            FileName = "brief.md",
            ExtractedText = "Enterprise AI web app with API, Foundry agent, file search, SQL, 120000 users, production, regulated PII.",
            WordCount = 16,
            Excerpt = "Enterprise AI web app..."
        });
        return await new OfflineEstimationEngine().EstimateAsync(job);
    }

    // ---------------------------------------------------------------- Pricing references

    [Fact]
    public async Task Every_cost_line_item_has_a_first_party_azure_pricing_reference()
    {
        var job = await SampleJobAsync();
        Assert.NotEmpty(job.Cost.LineItems);
        foreach (var li in job.Cost.LineItems)
        {
            Assert.False(string.IsNullOrWhiteSpace(li.PricingReferenceUrl), $"missing pricing url for {li.Service}");
            Assert.False(string.IsNullOrWhiteSpace(li.PricingReferenceLabel), $"missing pricing label for {li.Service}");
            Assert.StartsWith("https://azure.microsoft.com/", li.PricingReferenceUrl);
        }
    }

    [Fact]
    public void Pricing_catalog_resolves_known_services_to_canonical_pages()
    {
        Assert.Contains("app-service", AzurePricingCatalog.ResolvePricingReference("Azure App Service").Url);
        Assert.Contains("openai", AzurePricingCatalog.ResolvePricingReference("Microsoft Foundry (Azure OpenAI)").Url);
        Assert.Contains("search", AzurePricingCatalog.ResolvePricingReference("Azure AI Search").Url);
        // Unknown service still falls back to a valid first-party Azure pricing landing page (never empty).
        var fallback = AzurePricingCatalog.ResolvePricingReference("Some Brand New Service");
        Assert.StartsWith("https://azure.microsoft.com/", fallback.Url);
        Assert.False(string.IsNullOrWhiteSpace(fallback.Label));
    }

    // ---------------------------------------------------------------- Non-prod / prod / total model

    [Fact]
    public async Task Line_items_expose_nonprod_prod_and_total_costs_that_are_self_consistent()
    {
        var job = await SampleJobAsync();
        foreach (var li in job.Cost.LineItems)
        {
            // Non-prod is a scaled-down footprint: 0 <= nonProdQty <= prodQty.
            Assert.True(li.NonProdQuantity >= 0, $"nonprod qty negative for {li.Service}");
            Assert.True(li.NonProdQuantity <= li.Quantity + 0.0001m, $"nonprod qty exceeds prod qty for {li.Service}");
            // Per-line costs derive from qty * unit price.
            Assert.Equal(Math.Round(li.Quantity * li.UnitPrice, 2), Math.Round(li.ProdMonthlyCost, 2));
            Assert.Equal(Math.Round(li.NonProdQuantity * li.UnitPrice, 2), Math.Round(li.NonProdMonthlyCost, 2));
            Assert.Equal(Math.Round(li.ProdMonthlyCost + li.NonProdMonthlyCost, 2), Math.Round(li.TotalMonthlyCost, 2));
        }
    }

    [Fact]
    public async Task Estimate_rolls_up_nonprod_prod_and_combined_totals()
    {
        var job = await SampleJobAsync();
        var c = job.Cost;
        var prodRaw = c.LineItems.Sum(l => l.ProdMonthlyCost);
        var npRaw = c.LineItems.Sum(l => l.NonProdMonthlyCost);

        Assert.Equal(Math.Round(prodRaw, 2), Math.Round(c.ProdMonthlyTotal, 2));
        Assert.Equal(Math.Round(npRaw, 2), Math.Round(c.NonProdMonthlyTotal, 2));
        Assert.Equal(Math.Round(prodRaw + npRaw, 2), Math.Round(c.CombinedMonthlyTotal, 2));
        // Combined should be strictly greater than prod alone when any non-prod footprint exists.
        Assert.True(c.CombinedMonthlyTotal >= c.ProdMonthlyTotal);
    }

    // ---------------------------------------------------------------- Excel: env sheets + pricing column

    [Fact]
    public async Task Workbook_has_nonprod_prod_and_total_sheets()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        foreach (var name in new[] { "Non-Prod", "Prod", "Total" })
            Assert.True(wb.Worksheets.Contains(name), $"missing env sheet: {name}");
    }

    [Fact]
    public async Task Total_sheet_computes_nonprod_prod_and_total_with_formulas()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var total = wb.Worksheet("Total");

        var table = Assert.Single(total.Tables);
        Assert.Equal("TotalCost", table.Name);
        var firstData = table.DataRange.FirstRow().RowNumber();

        // NonProd Qty / Prod Qty are editable inputs (no formula).
        Assert.False(total.Cell(firstData, table.Field("NonProd Qty").Column.ColumnNumber()).HasFormula);
        Assert.False(total.Cell(firstData, table.Field("Prod Qty").Column.ColumnNumber()).HasFormula);

        // NonProd / Prod / Total costs are per-row A1 formulas (e.g. =F5*D5, =G5*D5, =H5+I5).
        var npQtyRef = ClosedXML.Excel.XLHelper.GetColumnLetterFromNumber(table.Field("NonProd Qty").Column.ColumnNumber()) + firstData;
        var prQtyRef = ClosedXML.Excel.XLHelper.GetColumnLetterFromNumber(table.Field("Prod Qty").Column.ColumnNumber()) + firstData;
        var priceRef = ClosedXML.Excel.XLHelper.GetColumnLetterFromNumber(table.Field("Unit Price").Column.ColumnNumber()) + firstData;
        var npCostRef = ClosedXML.Excel.XLHelper.GetColumnLetterFromNumber(table.Field("NonProd Cost").Column.ColumnNumber()) + firstData;
        var prCostRef = ClosedXML.Excel.XLHelper.GetColumnLetterFromNumber(table.Field("Prod Cost").Column.ColumnNumber()) + firstData;
        var np = total.Cell(firstData, table.Field("NonProd Cost").Column.ColumnNumber());
        var pr = total.Cell(firstData, table.Field("Prod Cost").Column.ColumnNumber());
        var tot = total.Cell(firstData, table.Field("Total Cost").Column.ColumnNumber());
        Assert.Contains(npQtyRef, np.FormulaA1);
        Assert.Contains(priceRef, np.FormulaA1);
        Assert.Contains(prQtyRef, pr.FormulaA1);
        Assert.Contains(npCostRef, tot.FormulaA1);
        Assert.Contains(prCostRef, tot.FormulaA1);

        // Three SUBTOTAL totals (NonProd / Prod / Total) auto-include inserted rows.
        Assert.True(table.ShowTotalsRow);
        foreach (var col in new[] { "NonProd Cost", "Prod Cost", "Total Cost" })
            Assert.Equal(XLTotalsRowFunction.Sum, table.Field(col).TotalsRowFunction);
    }

    [Fact]
    public async Task Env_defined_names_exist_for_summary_cross_reference()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        foreach (var n in new[] { "Proj37_NonProdMonthly", "Proj37_ProdMonthly", "Proj37_TotalMonthly" })
            Assert.True(wb.DefinedNames.TryGetValue(n, out _), $"missing env defined name: {n}");
    }

    [Fact]
    public async Task Cost_sheets_carry_a_pricing_reference_hyperlink_column()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);

        foreach (var sheetName in new[] { "Cost Model", "Non-Prod", "Prod", "Total" })
        {
            var ws = wb.Worksheet(sheetName);
            var table = ws.Tables.First();
            // "Pricing Reference" column header must exist on each cost sheet.
            var hasPricingCol = table.Fields.Any(f => f.Name == "Pricing Reference");
            Assert.True(hasPricingCol, $"{sheetName} is missing the Pricing Reference column");
            // At least one data cell in that column carries a hyperlink to an Azure pricing page.
            var col = table.Field("Pricing Reference").Column.ColumnNumber();
            var firstData = table.DataRange.FirstRow().RowNumber();
            var lastData = table.DataRange.LastRow().RowNumber();
            var anyLink = false;
            for (int r = firstData; r <= lastData; r++)
            {
                var cell = ws.Cell(r, col);
                if (cell.HasHyperlink && cell.GetHyperlink().ExternalAddress != null &&
                    cell.GetHyperlink().ExternalAddress.ToString().Contains("azure.microsoft.com"))
                { anyLink = true; break; }
            }
            Assert.True(anyLink, $"{sheetName} Pricing Reference column has no Azure pricing hyperlink");
        }
    }

    // ---------------------------------------------------------------- Markdown rendering

    [Fact]
    public void MarkdownRenderer_emits_html_for_headings_and_lists()
    {
        var html = new MarkdownRenderer().ToHtml("# Title\n\nSome **bold** text.\n\n- one\n- two\n");
        Assert.Contains("<h1", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<li>two</li>", html);
    }

    [Fact]
    public void MarkdownRenderer_is_xss_safe_disables_raw_html()
    {
        // Raw inline HTML in the source must NOT be passed through verbatim (DisableHtml in the pipeline).
        var html = new MarkdownRenderer().ToHtml("Hello <script>alert(1)</script> world");
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
    }
}
