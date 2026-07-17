using Xunit;
using ClosedXML.Excel;
using Proj37.CostEstimator.Web.Models;
using Proj37.CostEstimator.Web.Services;

namespace Proj37.CostEstimator.Tests;

public class ExcelReportGeneratorTests
{
    private static async Task<EstimationResult> SampleJobAsync()
    {
        var job = new EstimationResult();
        job.Documents.Add(new IngestedDocument { FileName = "brief.md", ExtractedText = "AI web app with API, Foundry agent, file search, SQL.", WordCount = 9, Excerpt = "AI web app..." });
        return await new OfflineEstimationEngine().EstimateAsync(job);
    }

    [Fact]
    public async Task Generate_returns_nonempty_xlsx_bytes()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 2000, $"workbook unexpectedly small: {bytes.Length} bytes");
        // XLSX is a zip; first two bytes are 'PK'.
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
    }

    [Fact]
    public async Task Workbook_has_expected_sheets()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);

        foreach (var name in new[] { "Summary", "Cost Model", "Requirements", "Scope", "Documents" })
            Assert.True(wb.Worksheets.Contains(name), $"missing sheet: {name}");
    }

    [Fact]
    public async Task Workbook_has_project_and_operation_cost_sheets()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);

        // The two new agentic-cost tabs must each be an editable table with a formula-driven cost column.
        var project = wb.Worksheet("Project Cost");
        var projectTable = Assert.Single(project.Tables);
        Assert.Equal("ProjectCost", projectTable.Name);
        Assert.True(projectTable.ShowTotalsRow);
        Assert.Equal(XLTotalsRowFunction.Sum, projectTable.Field("Cost").TotalsRowFunction);
        var projCostCell = project.Cell(projectTable.DataRange.FirstRow().RowNumber(), projectTable.Field("Cost").Column.ColumnNumber());
        Assert.True(projCostCell.HasFormula, "Project Cost must be a Day Rate * Estimated Days formula");

        var ops = wb.Worksheet("Operation Cost");
        var opsTable = Assert.Single(ops.Tables);
        Assert.Equal("OperationCost", opsTable.Name);
        Assert.True(opsTable.ShowTotalsRow);
        Assert.Equal(XLTotalsRowFunction.Sum, opsTable.Field("Monthly Cost").TotalsRowFunction);
        var opsCostCell = ops.Cell(opsTable.DataRange.FirstRow().RowNumber(), opsTable.Field("Monthly Cost").Column.ColumnNumber());
        Assert.True(opsCostCell.HasFormula, "Operation Monthly Cost must be a Quantity * Unit Price formula");
    }

    [Fact]
    public async Task CostModel_is_an_editable_table_with_formula_column_and_totals_row()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var cost = wb.Worksheet("Cost Model");

        // The line items must live in a native Excel Table named "CostModel".
        var table = Assert.Single(cost.Tables);
        Assert.Equal("CostModel", table.Name);

        // Quantity / Unit Price must be plain editable input cells (no formula); Monthly Cost is derived.
        var qtyHeader = table.Field("Quantity").Column.ColumnNumber();
        var priceHeader = table.Field("Unit Price").Column.ColumnNumber();
        var monthlyHeader = table.Field("Monthly Cost").Column.ColumnNumber();
        var firstData = table.DataRange.FirstRow().RowNumber();

        Assert.False(cost.Cell(firstData, qtyHeader).HasFormula, "Quantity must be a directly editable value, not a formula");
        Assert.False(cost.Cell(firstData, priceHeader).HasFormula, "Unit Price must be a directly editable value, not a formula");

        // Monthly Cost is a per-row A1 formula = Quantity * Unit Price (e.g. =F5*G5). Plain A1 formulas
        // are used instead of table structured references because Excel strips structured-reference
        // calculated columns on open; A1 formulas survive and keep recalculating.
        var monthlyCell = cost.Cell(firstData, monthlyHeader);
        Assert.True(monthlyCell.HasFormula, "Monthly Cost must be a formula");
        var qtyRef = ClosedXML.Excel.XLHelper.GetColumnLetterFromNumber(qtyHeader) + firstData;
        var priceRef = ClosedXML.Excel.XLHelper.GetColumnLetterFromNumber(priceHeader) + firstData;
        Assert.Contains(qtyRef, monthlyCell.FormulaA1);
        Assert.Contains(priceRef, monthlyCell.FormulaA1);
        Assert.Contains("*", monthlyCell.FormulaA1);

        // The table has a totals row that SUMs Monthly Cost (SUBTOTAL auto-includes new/inserted rows).
        Assert.True(table.ShowTotalsRow, "Cost Model table must show a totals row");
        Assert.Equal(XLTotalsRowFunction.Sum, table.Field("Monthly Cost").TotalsRowFunction);
        var totalsCell = table.TotalsRow().Cell(monthlyHeader);
        Assert.True(totalsCell.HasFormula);
        Assert.Contains("SUBTOTAL", totalsCell.FormulaA1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CostModel[Monthly Cost]", totalsCell.FormulaA1);
    }

    [Fact]
    public async Task Derived_totals_and_defined_names_track_the_table_total()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);

        // Workbook defined names exist for the four headline figures so the Summary survives table growth.
        foreach (var n in new[] { "Proj37_MonthlySubtotal", "Proj37_Contingency", "Proj37_MonthlyTotal", "Proj37_AnnualTotal" })
            Assert.True(wb.DefinedNames.TryGetValue(n, out _), $"missing defined name: {n}");

        var cost = wb.Worksheet("Cost Model");
        // Contingency + monthly total reference the table's totals cell via a structured reference.
        var contingency = wb.DefinedNames.ToList().First(d => d.Name == "Proj37_Contingency").Ranges.Single().FirstCell();
        var monthlyTotal = wb.DefinedNames.ToList().First(d => d.Name == "Proj37_MonthlyTotal").Ranges.Single().FirstCell();
        Assert.Contains("CostModel[[#Totals],[Monthly Cost]]", contingency.FormulaA1);
        Assert.Contains("CostModel[[#Totals],[Monthly Cost]]", monthlyTotal.FormulaA1);

        // Summary sheet references the defined names (not hard-coded cell addresses).
        var summary = wb.Worksheet("Summary");
        var summaryFormulas = summary.CellsUsed(c => c.HasFormula).Select(c => c.FormulaA1).ToList();
        Assert.Contains(summaryFormulas, f => f.Contains("Proj37_MonthlySubtotal"));
        Assert.Contains(summaryFormulas, f => f.Contains("Proj37_AnnualTotal"));
    }

    [Fact]
    public async Task Workbook_forces_full_recalc_on_load_and_omits_stale_calcChain()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);

        using var ms = new MemoryStream(bytes);
        using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);

        // The table part must be present (editable cost table).
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("xl/tables/", StringComparison.OrdinalIgnoreCase));

        // workbook.xml must tell Excel to fully recalculate on open. ClosedXML writes formula cells with
        // NO cached value, so without this Excel shows blank/0 in formula cells ("missing formulas") and
        // can raise a repair prompt over the uncached table totals / structured references.
        var wbEntry = zip.Entries.Single(e => e.FullName.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase));
        using var wbReader = new StreamReader(wbEntry.Open());
        var workbookXml = await wbReader.ReadToEndAsync();
        Assert.Contains("fullCalcOnLoad=\"1\"", workbookXml);
        Assert.Contains("calcId=\"0\"", workbookXml);

        // calcChain.xml is intentionally removed so Excel rebuilds it cleanly (a stale calcChain over
        // uncached ClosedXML formulas is itself an Excel repair trigger). Formulas are NOT lost - they
        // live in the sheet XML (asserted below).
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Equals("xl/calcChain.xml", StringComparison.OrdinalIgnoreCase));

        // The live formulas are present in the worksheet parts.
        var sheetXml = new System.Text.StringBuilder();
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var sr = new StreamReader(e.Open());
            sheetXml.Append(await sr.ReadToEndAsync());
        }
        var allSheets = sheetXml.ToString();
        Assert.Matches(@"<x:f>[A-Z]+\d+\*[A-Z]+\d+</x:f>", allSheets);
        Assert.Contains("SUBTOTAL(109", allSheets);
    }

    [Fact]
    public async Task Every_formula_cell_has_a_cached_value_so_excel_does_not_strip_formulas_or_tables()
    {
        // ClosedXML 0.104 writes formula cells WITHOUT a cached value (and its calc engine overflows
        // evaluating the table SUBTOTAL / structured references). Excel treats uncached formula cells and
        // the dependent table as damaged and removes them on open ("Removed Records: Formula ..." /
        // "Removed Records: Table ..."). The generator injects C#-computed results as cached <v> values,
        // so there must be zero formula cells left without a value in ANY worksheet.
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);

        using var ms = new MemoryStream(bytes);
        using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(ms, false);
        var wbPart = doc.WorkbookPart!;

        var offenders = new List<string>();
        foreach (var sheet in wbPart.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>())
        {
            var wsPart = (DocumentFormat.OpenXml.Packaging.WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
            foreach (var cell in wsPart.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>())
            {
                if (cell.CellFormula is null) continue;
                if (cell.CellValue is null || string.IsNullOrEmpty(cell.CellValue.Text))
                    offenders.Add($"{sheet.Name}!{cell.CellReference?.Value} = {cell.CellFormula.Text}");
            }
        }

        Assert.True(offenders.Count == 0, "formula cells missing a cached value:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public async Task Workbook_opens_without_excel_repair_strict_ooxml_validation_passes()
    {
        var job = await SampleJobAsync();
        var bytes = new ExcelReportGenerator().Generate(job);

        // Strict OOXML validation at the Office 2019 target. Zero errors => the package is well-formed
        // and Excel will not show the "we found a problem with some content" repair prompt on open.
        using var ms = new MemoryStream();
        ms.Write(bytes, 0, bytes.Length);
        ms.Position = 0;
        using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(ms, false);
        var validator = new DocumentFormat.OpenXml.Validation.OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019);
        var errors = validator.Validate(doc).ToList();
        Assert.True(errors.Count == 0, "OOXML validation errors:\n" + string.Join("\n", errors.Select(e => $"{e.Id} {e.Description} @ {e.Part?.Uri} {e.Path?.XPath}")));
    }
}
