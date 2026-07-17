using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Proj37.CostEstimator.Web.Models;
using System.Globalization;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Renders an <see cref="EstimationResult"/> into a multi-sheet Excel workbook with live formulas, so
/// reviewers can adjust quantities/unit prices and see totals recalculate. This is the primary
/// deliverable of the POC ("Excel calculation pages").
///
/// Sheets:
///   1. Summary        — headline numbers + metadata
///   2. Cost Model     — per-service line items in an editable Excel Table with QUANTITY*UNITPRICE
///                       formulas, a SUBTOTAL totals row, contingency, totals
///   3. Requirements   — derived technical requirements
///   4. Scope          — scope summary (in/out/assumptions)
///   5. Documents      — ingested source documents
///
/// Cost Model design notes (driven by reviewer feedback):
///   * The line items live in a native Excel <b>Table</b> ("CostModel"). <b>Quantity</b> and
///     <b>Unit Price</b> are plain, editable input cells (highlighted) — users type new numbers and
///     the workbook recalculates.
///   * <b>Monthly Cost</b> is a per-row A1 formula (=F5*G5, i.e. Quantity * Unit Price). Plain A1
///     formulas are used deliberately: Excel strips table structured-reference calculated columns
///     (=[@Quantity]*[@[Unit Price]]) on open ("Removed Records: Formula/Table"), whereas A1 formulas
///     always survive and keep recalculating when Quantity / Unit Price change.
///   * The totals row uses <c>SUBTOTAL(109, CostModel[Monthly Cost])</c>, so it automatically includes
///     <b>new / inserted line item rows</b> without the SUM range having to be edited. Reviewers can add
///     a manual line item by typing in the row directly beneath the last one (the table auto-extends) or
///     by inserting a row inside the table — totals keep working either way.
///   * Contingency / monthly total / annual total reference the totals cell and are exposed as workbook
///     <b>defined names</b> (Proj37_*) so the Summary sheet keeps tracking them even as the table grows.
/// </summary>
public sealed class ExcelReportGenerator
{
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#0B5394");
    private static readonly XLColor HeaderText = XLColor.White;
    private static readonly XLColor AccentFill = XLColor.FromHtml("#E8F0FE");
    private static readonly XLColor TotalFill = XLColor.FromHtml("#FFF2CC");
    private static readonly XLColor InputFill = XLColor.FromHtml("#FFFDE7");   // editable input cells (Qty / Unit Price)
    private static readonly XLColor InputBorder = XLColor.FromHtml("#E0A800");

    // Workbook-scoped defined names so the Summary sheet survives table growth / inserted rows.
    private const string NameSubtotal = "Proj37_MonthlySubtotal";
    private const string NameContingency = "Proj37_Contingency";
    private const string NameMonthlyTotal = "Proj37_MonthlyTotal";
    private const string NameAnnualTotal = "Proj37_AnnualTotal";
    private const string CostTableName = "CostModel";

    // Env-split sheet defined names (Non-Prod / Prod / Total).
    private const string NameNonProdMonthly = "Proj37_NonProdMonthly";
    private const string NameProdMonthly = "Proj37_ProdMonthly";
    private const string NameTotalMonthly = "Proj37_TotalMonthly";

    // Cached formula results, keyed by "SheetName!A1". ClosedXML 0.104 writes every formula cell WITHOUT
    // a cached value (and its calc engine stack-overflows evaluating the table SUBTOTAL / structured
    // references, so we can't ask it to compute them). Excel treats formula cells that have no cached
    // value — together with the table calculated column that depends on them — as damaged and strips them
    // on open ("Removed Records: Formula …" / "Removed Records: Table …"). We compute the results here in
    // C# and inject them as cached <v> values during the post-save OOXML fixup so the workbook opens clean.
    private readonly Dictionary<string, double> _cachedValues = new();

    private void Cache(IXLCell cell, double value)
        => _cachedValues[$"{cell.Worksheet.Name}!{cell.Address.ToStringRelative()}"] = value;

    public byte[] Generate(EstimationResult r)
    {
        using var wb = new XLWorkbook();
        // Excel must recalculate every formula when the workbook is opened. ClosedXML writes formula
        // cells WITHOUT cached values, so without a forced recalc Excel shows blank/0 in formula cells
        // ("missing formulas") and — more seriously — strips the uncached formulas and the table that
        // depends on them ("Removed Records: Formula/Table"). We inject C#-computed cached values in the
        // post-save fixup and stamp Auto calc mode + fullCalcOnLoad (below) so Excel opens clean and still
        // recomputes on load.
        wb.CalculateMode = XLCalculateMode.Auto;

        BuildSummarySheet(wb, r);
        // Primary editable cost sheet (production sizing) — keeps the original "Cost Model" tab + defined names.
        var costSheet = BuildCostSheet(wb, r);
        // Environment-split sheets with working formulas: Non-Prod, Prod, and a combined Total.
        BuildEnvCostSheet(wb, r, EnvKind.NonProd);
        BuildEnvCostSheet(wb, r, EnvKind.Prod);
        BuildTotalCostSheet(wb, r);
        BuildProjectCostSheet(wb, r);
        BuildOperationCostSheet(wb, r);
        BuildRequirementsSheet(wb, r);
        BuildScopeSheet(wb, r);
        BuildDocumentsSheet(wb, r);

        wb.Worksheet("Summary").SetTabActive();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ForceFullRecalcOnLoad(ms.ToArray());
    }

    /// <summary>
    /// Post-save OOXML fixup. Two problems are corrected here:
    /// <list type="number">
    /// <item>ClosedXML 0.104 emits every formula cell with <b>no cached value</b> (its calc engine
    /// stack-overflows on the table SUBTOTAL / structured references, so it can't compute them). Excel
    /// treats such formula cells — and the table calculated column that depends on them — as damaged and
    /// strips them on open ("Removed Records: Formula …" / "Removed Records: Table …"). We inject the
    /// results we computed in C# (<see cref="_cachedValues"/>) as cached <c>&lt;v&gt;</c> values.</item>
    /// <item>We set <c>fullCalcOnLoad="1"</c> / <c>calcId="0"</c> so Excel still recomputes everything
    /// on open (keeping the cached values honest if a reviewer edits inputs), and drop the stale
    /// <c>calcChain.xml</c> that ClosedXML leaves inconsistent with the uncached cells.</item>
    /// </list>
    /// </summary>
    private byte[] ForceFullRecalcOnLoad(byte[] xlsx)
    {
        using var ms = new MemoryStream();
        ms.Write(xlsx, 0, xlsx.Length);
        ms.Position = 0;
        using (var doc = SpreadsheetDocument.Open(ms, isEditable: true))
        {
            var wbPart = doc.WorkbookPart ?? throw new InvalidOperationException("Workbook part missing.");
            var workbook = wbPart.Workbook;
            var calcPr = workbook.CalculationProperties;
            if (calcPr is null)
            {
                calcPr = new CalculationProperties();
                // CalculationProperties must sit after definedNames/sheets per the schema; AppendChild is safe.
                workbook.AppendChild(calcPr);
            }
            // calcId 0 => "produced by an unknown/older engine" => Excel does a full recalc.
            calcPr.CalculationId = 0U;
            calcPr.FullCalculationOnLoad = true;
            calcPr.ForceFullCalculation = true;
            calcPr.CalculationMode = CalculateModeValues.Auto;
            workbook.Save();

            InjectCachedValues(wbPart);
            StripCalculatedColumnFormulas(wbPart);

            // Remove the calculation chain. ClosedXML writes formula cells with no cached value, which
            // makes the emitted calcChain.xml inconsistent with the cells — a classic Excel repair
            // trigger ("Removed Records: Formula referenced from /xl/calcChain.xml part"). Excel safely
            // rebuilds the chain from scratch on open when the part is absent, so deleting it removes the
            // repair prompt without losing any formulas.
            if (wbPart.CalculationChainPart is not null)
                wbPart.DeletePart(wbPart.CalculationChainPart);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the C#-computed formula results (<see cref="_cachedValues"/>) into the matching formula
    /// cells as cached numeric <c>&lt;v&gt;</c> values, so Excel has a valid result for every formula and
    /// does not strip the formulas / dependent table on open.
    /// </summary>
    private void InjectCachedValues(WorkbookPart wbPart)
    {
        if (_cachedValues.Count == 0) return;
        foreach (var sheet in wbPart.Workbook.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>())
        {
            if (sheet.Id?.Value is not { } relId || sheet.Name?.Value is not { } sheetName) continue;
            if (wbPart.GetPartById(relId) is not WorksheetPart wsPart) continue;

            var changed = false;
            foreach (var cell in wsPart.Worksheet.Descendants<Cell>())
            {
                if (cell.CellFormula is null || cell.CellReference?.Value is not { } addr) continue;
                if (cell.CellValue is not null) continue; // already has a cached value
                if (!_cachedValues.TryGetValue($"{sheetName}!{addr}", out var value)) continue;

                cell.DataType = null; // numeric (default) — clear any text type
                cell.CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture));
                changed = true;
            }
            if (changed) wsPart.Worksheet.Save();
        }
    }

    /// <summary>
    /// Removes the <c>&lt;calculatedColumnFormula&gt;</c> from every table column. ClosedXML derives a
    /// calculated-column formula from the first data row's formula (e.g. <c>F5*G5</c>). A calculated
    /// column stored as a raw A1 formula is an unusual, repair-prone shape (Excel normally uses structured
    /// references for calc columns). We already write an explicit A1 formula into every data cell, so the
    /// calculated-column declaration is redundant — dropping it turns the range into a plain table whose
    /// formula cells stand on their own and open without any repair prompt.
    /// </summary>
    private static void StripCalculatedColumnFormulas(WorkbookPart wbPart)
    {
        foreach (var wsPart in wbPart.WorksheetParts)
        {
            foreach (var tablePart in wsPart.TableDefinitionParts)
            {
                var formulas = tablePart.Table.Descendants<CalculatedColumnFormula>().ToList();
                if (formulas.Count == 0) continue;
                foreach (var f in formulas) f.Remove();
                tablePart.Table.Save();
            }
        }
    }

    private enum EnvKind { NonProd, Prod }

    private void BuildSummarySheet(XLWorkbook wb, EstimationResult r)
    {
        var ws = wb.Worksheets.Add("Summary");
        ws.Column(1).Width = 28;
        ws.Column(2).Width = 60;

        Title(ws, "A1:B1", "Azure Cost Estimation — POC");
        ws.Cell("A2").Value = "Generated (UTC)";
        ws.Cell("B2").Value = r.CreatedUtc.UtcDateTime;
        ws.Cell("B2").Style.DateFormat.Format = "yyyy-mm-dd hh:mm";

        var rows = new (string Label, XLCellValue Value)[]
        {
            ("Project", r.Scope.ProjectName),
            ("Job ID", r.JobId),
            ("Estimation engine", r.Engine),
            ("Workload profile", r.Scope.WorkloadProfile),
            ("Expected scale", r.Scope.ExpectedScale),
            ("Data sensitivity", r.Scope.DataSensitivity),
            ("Environment", r.Scope.Environment),
            ("Region", r.Cost.Region),
            ("Currency", r.Cost.Currency),
            ("Documents ingested", r.Documents.Count),
            ("Technical requirements", r.Requirements.Count),
            ("Cost line items", r.Cost.LineItems.Count),
        };
        int row = 4;
        foreach (var (label, value) in rows)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = value;
            row++;
        }

        row++;
        SectionHeader(ws, $"A{row}:B{row}", "Headline cost (reference, not a quote)");
        row++;
        // Compute the same figures the formulas produce, so we can inject cached values (ClosedXML can't).
        decimal cFactor = 1 + r.Cost.ContingencyPercent / 100m;
        decimal prodSubtotal = r.Cost.LineItems.Sum(l => l.Quantity * l.UnitPrice);
        decimal nonProdSubtotal = r.Cost.LineItems.Sum(l => l.NonProdQuantity * l.UnitPrice);
        decimal totalSubtotal = prodSubtotal + nonProdSubtotal;
        decimal prodContingency = prodSubtotal * (r.Cost.ContingencyPercent / 100m);
        // Pull totals from the Cost Model sheet (via workbook defined names) so Summary tracks edits
        // and survives users adding / inserting line item rows in the table.
        ws.Cell(row, 1).Value = "Monthly subtotal"; ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).FormulaA1 = $"={NameSubtotal}"; MoneyCell(ws.Cell(row, 2), r.Cost.Currency); Cache(ws.Cell(row, 2), (double)prodSubtotal); row++;
        ws.Cell(row, 1).Value = $"Contingency ({r.Cost.ContingencyPercent}%)"; ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).FormulaA1 = $"={NameContingency}"; MoneyCell(ws.Cell(row, 2), r.Cost.Currency); Cache(ws.Cell(row, 2), (double)prodContingency); row++;
        ws.Cell(row, 1).Value = "Monthly total (incl. contingency)"; ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).FormulaA1 = $"={NameMonthlyTotal}"; MoneyCell(ws.Cell(row, 2), r.Cost.Currency); Cache(ws.Cell(row, 2), (double)(prodSubtotal + prodContingency));
        ws.Range($"A{row}:B{row}").Style.Fill.BackgroundColor = TotalFill; row++;
        ws.Cell(row, 1).Value = "Annual total (incl. contingency)"; ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).FormulaA1 = $"={NameAnnualTotal}"; MoneyCell(ws.Cell(row, 2), r.Cost.Currency); Cache(ws.Cell(row, 2), (double)((prodSubtotal + prodContingency) * 12));
        ws.Range($"A{row}:B{row}").Style.Fill.BackgroundColor = TotalFill; row += 2;

        SectionHeader(ws, $"A{row}:B{row}", "Cost by environment (monthly, incl. contingency)");
        row++;
        ws.Cell(row, 1).Value = "Non-Prod monthly"; ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).FormulaA1 = $"={NameNonProdMonthly}*{cFactor}"; MoneyCell(ws.Cell(row, 2), r.Cost.Currency); Cache(ws.Cell(row, 2), (double)(nonProdSubtotal * cFactor)); row++;
        ws.Cell(row, 1).Value = "Prod monthly"; ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).FormulaA1 = $"={NameProdMonthly}*{cFactor}"; MoneyCell(ws.Cell(row, 2), r.Cost.Currency); Cache(ws.Cell(row, 2), (double)(prodSubtotal * cFactor)); row++;
        ws.Cell(row, 1).Value = "Total monthly (Non-Prod + Prod)"; ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).FormulaA1 = $"={NameTotalMonthly}*{cFactor}"; MoneyCell(ws.Cell(row, 2), r.Cost.Currency); Cache(ws.Cell(row, 2), (double)(totalSubtotal * cFactor));
        ws.Range($"A{row}:B{row}").Style.Fill.BackgroundColor = TotalFill; row += 2;

        SectionHeader(ws, $"A{row}:B{row}", "Disclaimer");
        row++;
        ws.Cell(row, 1).Value = "Note";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).Value = r.Cost.PricingBasis + ". Figures are POC reference estimates derived from supplied documents and should be validated against the Azure Pricing Calculator / Retail Prices API before any commitment.";
        ws.Cell(row, 2).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 60;
    }

    private IXLWorksheet BuildCostSheet(XLWorkbook wb, EstimationResult r)
    {
        var ws = wb.Worksheets.Add("Cost Model");

        // Guidance banner so reviewers know exactly how to drive the sheet.
        Title(ws, "A1:I1", "Azure Cost Model — editable");
        ws.Cell("A2").Value =
            "Tip: edit the highlighted Quantity / Unit Price cells and totals recalculate automatically. " +
            "To add a manual line item, type in the empty row directly below the last one — the table grows " +
            "and the Monthly subtotal includes it (or insert a row inside the table). Do not delete the totals row.";
        ws.Range("A2:I2").Merge();
        ws.Cell("A2").Style.Alignment.WrapText = true;
        ws.Cell("A2").Style.Font.Italic = true;
        ws.Cell("A2").Style.Font.FontColor = XLColor.FromHtml("#444444");
        ws.Range("A2:I2").Style.Fill.BackgroundColor = AccentFill;
        ws.Row(2).Height = 42;

        string[] headers = { "Category", "Service", "SKU / Tier", "Meter", "Assumption", "Quantity", "Unit Price", "Unit", "Monthly Cost", "Pricing Reference" };
        const int colQty = 6, colUnitPrice = 7, colMonthly = 9, colPricingRef = 10;
        int headerRow = 4;
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = headers[c];
            HeaderStyle(cell);
        }

        int row = headerRow + 1;
        int firstDataRow = row;
        var ordered = r.Cost.LineItems.OrderBy(l => l.Category).ThenBy(l => l.Service).ToList();
        // Guarantee at least one data row so the table is always valid even with an empty estimate.
        if (ordered.Count == 0) ordered.Add(new CostLineItem { Category = "", Service = "" });
        foreach (var li in ordered)
        {
            ws.Cell(row, 1).Value = li.Category;
            ws.Cell(row, 2).Value = li.Service;
            ws.Cell(row, 3).Value = li.Sku;
            ws.Cell(row, 4).Value = li.Meter;
            ws.Cell(row, 5).Value = li.Assumption;
            ws.Cell(row, colQty).Value = li.Quantity;            // editable input
            ws.Cell(row, colUnitPrice).Value = li.UnitPrice;     // editable input
            ws.Cell(row, 8).Value = li.Unit;
            // Monthly Cost left blank here; the table calculated-column formula fills it (incl. future rows).
            // Pricing reference — a first-party Azure pricing link so each line is auditable in the workbook.
            WritePricingRef(ws.Cell(row, colPricingRef), li);
            row++;
        }
        int lastDataRow = row - 1;

        // ---- Build the editable Excel Table (ListObject) over header + data rows ----
        var table = ws.Range(headerRow, 1, lastDataRow, headers.Length).CreateTable(CostTableName);
        table.Theme = XLTableTheme.TableStyleLight9;
        // Monthly Cost = Quantity * Unit Price, written as a plain A1 formula (e.g. =F5*G5) on every data
        // row. A1 formulas are the most robust form Excel accepts: unlike a table structured-reference
        // calculated column (=[@Quantity]*[@[Unit Price]]), Excel never strips them on open, so the
        // formula bar always shows a live formula that recalculates when Quantity / Unit Price change.
        double subtotal = 0;
        foreach (var dataRow in table.DataRange.Rows())
        {
            var monthlyCell = dataRow.Cell(colMonthly);
            int rowNum = monthlyCell.Address.RowNumber;
            monthlyCell.FormulaA1 = $"={XLHelper.GetColumnLetterFromNumber(colQty)}{rowNum}*{XLHelper.GetColumnLetterFromNumber(colUnitPrice)}{rowNum}";
            double monthly = dataRow.Cell(colQty).GetDouble() * dataRow.Cell(colUnitPrice).GetDouble();
            Cache(monthlyCell, monthly);
            subtotal += monthly;
        }

        // Totals row: SUBTOTAL(109, CostModel[Monthly Cost]) auto-includes new rows.
        table.ShowTotalsRow = true;
        table.Field("Category").TotalsRowLabel = "Monthly subtotal";
        table.Field("Monthly Cost").TotalsRowFunction = XLTotalsRowFunction.Sum;
        int totalsRow = table.TotalsRow().RowNumber();
        var subtotalCell = ws.Cell(totalsRow, colMonthly);
        MoneyCell(subtotalCell, r.Cost.Currency);
        Cache(subtotalCell, subtotal);
        ws.Cell(totalsRow, 1).Style.Font.Bold = true;

        // Format + highlight the editable input columns so reviewers know what to change.
        var qtyData = ws.Range(firstDataRow, colQty, lastDataRow, colQty);
        var priceData = ws.Range(firstDataRow, colUnitPrice, lastDataRow, colUnitPrice);
        qtyData.Style.NumberFormat.Format = "#,##0.######";
        foreach (var c in priceData.Cells()) MoneyCell(c, r.Cost.Currency, "#,##0.000000");
        foreach (var rng in new[] { qtyData, priceData })
        {
            rng.Style.Fill.BackgroundColor = InputFill;
            rng.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.OutsideBorderColor = InputBorder;
        }
        // Mark the Monthly Cost column (derived) italic and money-formatted.
        var monthlyData = ws.Range(firstDataRow, colMonthly, lastDataRow, colMonthly);
        monthlyData.Style.Font.Italic = true;
        foreach (var c in monthlyData.Cells()) MoneyCell(c, r.Cost.Currency);

        // ---- Derived totals below the table, exposed as workbook defined names ----
        int contingencyRow = totalsRow + 2;
        int totalRowNo = contingencyRow + 1;
        int annualRow = totalRowNo + 1;

        ws.Cell(contingencyRow, 1).Value = $"Contingency ({r.Cost.ContingencyPercent}%)";
        ws.Cell(contingencyRow, 1).Style.Font.Bold = true;
        var contingencyCell = ws.Cell(contingencyRow, 2);
        contingencyCell.FormulaA1 = $"={CostTableName}[[#Totals],[Monthly Cost]]*{r.Cost.ContingencyPercent / 100m}";
        MoneyCell(contingencyCell, r.Cost.Currency);
        double contingency = subtotal * (double)(r.Cost.ContingencyPercent / 100m);
        Cache(contingencyCell, contingency);

        ws.Cell(totalRowNo, 1).Value = "Monthly total (incl. contingency)";
        ws.Cell(totalRowNo, 1).Style.Font.Bold = true;
        var totalCell = ws.Cell(totalRowNo, 2);
        totalCell.FormulaA1 = $"={CostTableName}[[#Totals],[Monthly Cost]]+B{contingencyRow}";
        MoneyCell(totalCell, r.Cost.Currency);
        Cache(totalCell, subtotal + contingency);
        ws.Range(totalRowNo, 1, totalRowNo, 2).Style.Fill.BackgroundColor = TotalFill;

        ws.Cell(annualRow, 1).Value = "Annual total (incl. contingency)";
        ws.Cell(annualRow, 1).Style.Font.Bold = true;
        var annualCell = ws.Cell(annualRow, 2);
        annualCell.FormulaA1 = $"=B{totalRowNo}*12";
        MoneyCell(annualCell, r.Cost.Currency);
        Cache(annualCell, (subtotal + contingency) * 12);
        ws.Range(annualRow, 1, annualRow, 2).Style.Fill.BackgroundColor = TotalFill;

        // Defined names (workbook scope) so the Summary sheet references survive table growth.
        wb.DefinedNames.Add(NameSubtotal, subtotalCell.AsRange());
        wb.DefinedNames.Add(NameContingency, contingencyCell.AsRange());
        wb.DefinedNames.Add(NameMonthlyTotal, totalCell.AsRange());
        wb.DefinedNames.Add(NameAnnualTotal, annualCell.AsRange());

        // Notes
        int noteRow = annualRow + 2;
        ws.Cell(noteRow, 1).Value = "Notes";
        ws.Cell(noteRow, 1).Style.Font.Bold = true;
        foreach (var note in r.Cost.Notes)
        {
            noteRow++;
            ws.Cell(noteRow, 1).Value = "• " + note;
        }
        noteRow++;
        ws.Cell(noteRow, 1).Value =
            "• How to add a line item: select the last data row, press Tab/Enter to spill into the next row, " +
            "then type your Category/Service plus a Quantity and Unit Price. Monthly Cost and the Monthly subtotal update automatically.";
        ws.Cell(noteRow, 1).Style.Font.Italic = true;

        // NOTE: do NOT call AdjustToContents() on this sheet. Auto-sizing forces ClosedXML to render
        // every cell value, which makes its calc engine evaluate the table totals-row SUBTOTAL formula.
        // ClosedXML (0.104) overflows the stack evaluating SUBTOTAL over a table column, so we set
        // explicit, readable column widths instead. Excel still computes the formula correctly on open.
        double[] widths = { 16, 22, 14, 16, 45, 12, 14, 16, 16, 22 };
        for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ws.SheetView.FreezeRows(headerRow);
        return ws;
    }

    // ---------------------------------------------------------------- Env-split cost sheets

    /// <summary>
    /// Builds a single-environment editable cost sheet (Non-Prod or Prod). Same editable-table pattern as
    /// the primary Cost Model: Quantity * Unit Price calculated column, SUBTOTAL totals row, contingency
    /// and monthly/annual totals. The chosen environment selects which quantity drives the math
    /// (NonProdQuantity vs Quantity). Exposes a defined name for the env monthly subtotal so Summary tracks it.
    /// </summary>
    private void BuildEnvCostSheet(XLWorkbook wb, EstimationResult r, EnvKind env)
    {
        bool nonProd = env == EnvKind.NonProd;
        string sheetName = nonProd ? "Non-Prod" : "Prod";
        string tableName = nonProd ? "NonProdCost" : "ProdCost";
        string definedName = nonProd ? NameNonProdMonthly : NameProdMonthly;

        var ws = wb.Worksheets.Add(sheetName);
        Title(ws, "A1:J1", $"{sheetName} environment — monthly cost");
        ws.Cell("A2").Value = nonProd
            ? "Scaled-down dev/test/POC footprint of the same architecture."
            : "Full production sizing for the live workload.";
        ws.Cell("A2").Style.Font.Italic = true;

        string[] headers = { "Category", "Service", "SKU / Tier", "Meter", "Assumption", "Quantity", "Unit Price", "Unit", "Monthly Cost", "Pricing Reference" };
        const int colQty = 6, colUnitPrice = 7, colMonthly = 9, colPricingRef = 10;
        int headerRow = 4;
        for (int c = 0; c < headers.Length; c++) { var cell = ws.Cell(headerRow, c + 1); cell.Value = headers[c]; HeaderStyle(cell); }

        int row = headerRow + 1;
        int firstDataRow = row;
        var ordered = r.Cost.LineItems.OrderBy(l => l.Category).ThenBy(l => l.Service).ToList();
        if (ordered.Count == 0) ordered.Add(new CostLineItem { Category = "", Service = "" });
        foreach (var li in ordered)
        {
            ws.Cell(row, 1).Value = li.Category;
            ws.Cell(row, 2).Value = li.Service;
            ws.Cell(row, 3).Value = li.Sku;
            ws.Cell(row, 4).Value = li.Meter;
            ws.Cell(row, 5).Value = li.Assumption;
            ws.Cell(row, colQty).Value = nonProd ? li.NonProdQuantity : li.Quantity;  // editable input
            ws.Cell(row, colUnitPrice).Value = li.UnitPrice;                          // editable input
            ws.Cell(row, 8).Value = li.Unit;
            WritePricingRef(ws.Cell(row, colPricingRef), li);
            row++;
        }
        int lastDataRow = row - 1;

        var table = ws.Range(headerRow, 1, lastDataRow, headers.Length).CreateTable(tableName);
        table.Theme = nonProd ? XLTableTheme.TableStyleLight11 : XLTableTheme.TableStyleLight9;
        double subtotal = 0;
        foreach (var dataRow in table.DataRange.Rows())
        {
            var monthlyCell = dataRow.Cell(colMonthly);
            int rowNum = monthlyCell.Address.RowNumber;
            monthlyCell.FormulaA1 = $"={XLHelper.GetColumnLetterFromNumber(colQty)}{rowNum}*{XLHelper.GetColumnLetterFromNumber(colUnitPrice)}{rowNum}";
            double monthly = dataRow.Cell(colQty).GetDouble() * dataRow.Cell(colUnitPrice).GetDouble();
            Cache(monthlyCell, monthly);
            subtotal += monthly;
        }

        table.ShowTotalsRow = true;
        table.Field("Category").TotalsRowLabel = "Monthly subtotal";
        table.Field("Monthly Cost").TotalsRowFunction = XLTotalsRowFunction.Sum;
        int totalsRow = table.TotalsRow().RowNumber();
        var subtotalCell = ws.Cell(totalsRow, colMonthly);
        MoneyCell(subtotalCell, r.Cost.Currency);
        Cache(subtotalCell, subtotal);
        ws.Cell(totalsRow, 1).Style.Font.Bold = true;

        var qtyData = ws.Range(firstDataRow, colQty, lastDataRow, colQty);
        var priceData = ws.Range(firstDataRow, colUnitPrice, lastDataRow, colUnitPrice);
        qtyData.Style.NumberFormat.Format = "#,##0.######";
        foreach (var c in priceData.Cells()) MoneyCell(c, r.Cost.Currency, "#,##0.000000");
        foreach (var rng in new[] { qtyData, priceData })
        {
            rng.Style.Fill.BackgroundColor = InputFill;
            rng.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.OutsideBorderColor = InputBorder;
        }
        var monthlyData = ws.Range(firstDataRow, colMonthly, lastDataRow, colMonthly);
        monthlyData.Style.Font.Italic = true;
        foreach (var c in monthlyData.Cells()) MoneyCell(c, r.Cost.Currency);

        int contingencyRow = totalsRow + 2;
        int totalRowNo = contingencyRow + 1;
        int annualRow = totalRowNo + 1;

        ws.Cell(contingencyRow, 1).Value = $"Contingency ({r.Cost.ContingencyPercent}%)";
        ws.Cell(contingencyRow, 1).Style.Font.Bold = true;
        var contingencyCell = ws.Cell(contingencyRow, 2);
        contingencyCell.FormulaA1 = $"={tableName}[[#Totals],[Monthly Cost]]*{r.Cost.ContingencyPercent / 100m}";
        MoneyCell(contingencyCell, r.Cost.Currency);
        double contingency = subtotal * (double)(r.Cost.ContingencyPercent / 100m);
        Cache(contingencyCell, contingency);

        ws.Cell(totalRowNo, 1).Value = "Monthly total (incl. contingency)";
        ws.Cell(totalRowNo, 1).Style.Font.Bold = true;
        var totalCell = ws.Cell(totalRowNo, 2);
        totalCell.FormulaA1 = $"={tableName}[[#Totals],[Monthly Cost]]+B{contingencyRow}";
        MoneyCell(totalCell, r.Cost.Currency);
        Cache(totalCell, subtotal + contingency);
        ws.Range(totalRowNo, 1, totalRowNo, 2).Style.Fill.BackgroundColor = TotalFill;

        ws.Cell(annualRow, 1).Value = "Annual total (incl. contingency)";
        ws.Cell(annualRow, 1).Style.Font.Bold = true;
        var annualCell = ws.Cell(annualRow, 2);
        annualCell.FormulaA1 = $"=B{totalRowNo}*12";
        MoneyCell(annualCell, r.Cost.Currency);
        Cache(annualCell, (subtotal + contingency) * 12);
        ws.Range(annualRow, 1, annualRow, 2).Style.Fill.BackgroundColor = TotalFill;

        // Defined name for the env monthly subtotal (used by Summary + Total sheet cross-references).
        wb.DefinedNames.Add(definedName, subtotalCell.AsRange());

        double[] widths = { 16, 22, 14, 16, 45, 12, 14, 16, 16, 22 };
        for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ws.SheetView.FreezeRows(headerRow);
    }

    /// <summary>
    /// Builds the combined Total sheet: per line, Non-Prod and Prod quantities are editable inputs, and
    /// the sheet computes Non-Prod = NonProdQty*UnitPrice, Prod = ProdQty*UnitPrice, and Total = Non-Prod +
    /// Prod with live formulas. Three SUBTOTAL totals (Non-Prod / Prod / Total) auto-include inserted rows.
    /// Exposes the combined monthly subtotal as a defined name for the Summary sheet.
    /// </summary>
    private void BuildTotalCostSheet(XLWorkbook wb, EstimationResult r)
    {
        const string tableName = "TotalCost";
        var ws = wb.Worksheets.Add("Total");
        Title(ws, "A1:J1", "Total cost of ownership — Non-Prod + Prod");
        ws.Cell("A2").Value = "Edit either quantity column; Non-Prod, Prod and Total costs recalculate automatically.";
        ws.Cell("A2").Style.Font.Italic = true;

        // Columns: Category, Service, SKU, Unit Price, Unit, NonProd Qty, Prod Qty, NonProd Cost, Prod Cost, Total Cost, Pricing Reference
        string[] headers = { "Category", "Service", "SKU / Tier", "Unit Price", "Unit", "NonProd Qty", "Prod Qty", "NonProd Cost", "Prod Cost", "Total Cost", "Pricing Reference" };
        const int colUnitPrice = 4, colNpQty = 6, colPrQty = 7, colNpCost = 8, colPrCost = 9, colTotal = 10, colPricingRef = 11;
        int headerRow = 4;
        for (int c = 0; c < headers.Length; c++) { var cell = ws.Cell(headerRow, c + 1); cell.Value = headers[c]; HeaderStyle(cell); }

        int row = headerRow + 1;
        int firstDataRow = row;
        var ordered = r.Cost.LineItems.OrderBy(l => l.Category).ThenBy(l => l.Service).ToList();
        if (ordered.Count == 0) ordered.Add(new CostLineItem { Category = "", Service = "" });
        foreach (var li in ordered)
        {
            ws.Cell(row, 1).Value = li.Category;
            ws.Cell(row, 2).Value = li.Service;
            ws.Cell(row, 3).Value = li.Sku;
            ws.Cell(row, colUnitPrice).Value = li.UnitPrice;      // editable input
            ws.Cell(row, 5).Value = li.Unit;
            ws.Cell(row, colNpQty).Value = li.NonProdQuantity;    // editable input
            ws.Cell(row, colPrQty).Value = li.Quantity;          // editable input
            WritePricingRef(ws.Cell(row, colPricingRef), li);
            row++;
        }
        int lastDataRow = row - 1;

        var table = ws.Range(headerRow, 1, lastDataRow, headers.Length).CreateTable(tableName);
        table.Theme = XLTableTheme.TableStyleLight10;
        // Per-row costs derive from the editable qty + unit price columns via plain A1 formulas
        // (e.g. NonProd Cost =F5*D5, Prod Cost =G5*D5, Total Cost =H5+I5). A1 formulas survive Excel's
        // open-time checks where table structured-reference calculated columns get stripped.
        double npSubtotal = 0, prSubtotal = 0, totalColSubtotal = 0;
        foreach (var dataRow in table.DataRange.Rows())
        {
            var npCostCell = dataRow.Cell(colNpCost);
            var prCostCell = dataRow.Cell(colPrCost);
            var totalCostCell = dataRow.Cell(colTotal);
            int rowNum = npCostCell.Address.RowNumber;
            string priceRef = $"{XLHelper.GetColumnLetterFromNumber(colUnitPrice)}{rowNum}";
            npCostCell.FormulaA1 = $"={XLHelper.GetColumnLetterFromNumber(colNpQty)}{rowNum}*{priceRef}";
            prCostCell.FormulaA1 = $"={XLHelper.GetColumnLetterFromNumber(colPrQty)}{rowNum}*{priceRef}";
            totalCostCell.FormulaA1 = $"={XLHelper.GetColumnLetterFromNumber(colNpCost)}{rowNum}+{XLHelper.GetColumnLetterFromNumber(colPrCost)}{rowNum}";
            double price = dataRow.Cell(colUnitPrice).GetDouble();
            double npCost = dataRow.Cell(colNpQty).GetDouble() * price;
            double prCost = dataRow.Cell(colPrQty).GetDouble() * price;
            Cache(npCostCell, npCost);
            Cache(prCostCell, prCost);
            Cache(totalCostCell, npCost + prCost);
            npSubtotal += npCost;
            prSubtotal += prCost;
            totalColSubtotal += npCost + prCost;
        }

        table.ShowTotalsRow = true;
        table.Field("Category").TotalsRowLabel = "Monthly subtotal";
        table.Field("NonProd Cost").TotalsRowFunction = XLTotalsRowFunction.Sum;
        table.Field("Prod Cost").TotalsRowFunction = XLTotalsRowFunction.Sum;
        table.Field("Total Cost").TotalsRowFunction = XLTotalsRowFunction.Sum;
        int totalsRow = table.TotalsRow().RowNumber();
        ws.Cell(totalsRow, 1).Style.Font.Bold = true;
        foreach (var cc in new[] { colNpCost, colPrCost, colTotal }) MoneyCell(ws.Cell(totalsRow, cc), r.Cost.Currency);
        Cache(ws.Cell(totalsRow, colNpCost), npSubtotal);
        Cache(ws.Cell(totalsRow, colPrCost), prSubtotal);
        Cache(ws.Cell(totalsRow, colTotal), totalColSubtotal);
        var totalSubtotalCell = ws.Cell(totalsRow, colTotal);

        // Format + highlight editable inputs.
        var npQtyData = ws.Range(firstDataRow, colNpQty, lastDataRow, colNpQty);
        var prQtyData = ws.Range(firstDataRow, colPrQty, lastDataRow, colPrQty);
        var priceData = ws.Range(firstDataRow, colUnitPrice, lastDataRow, colUnitPrice);
        foreach (var rng in new[] { npQtyData, prQtyData }) rng.Style.NumberFormat.Format = "#,##0.######";
        foreach (var c in priceData.Cells()) MoneyCell(c, r.Cost.Currency, "#,##0.000000");
        foreach (var rng in new[] { npQtyData, prQtyData, priceData })
        {
            rng.Style.Fill.BackgroundColor = InputFill;
            rng.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.OutsideBorderColor = InputBorder;
        }
        foreach (var cc in new[] { colNpCost, colPrCost, colTotal })
        {
            var rng = ws.Range(firstDataRow, cc, lastDataRow, cc);
            rng.Style.Font.Italic = true;
            foreach (var c in rng.Cells()) MoneyCell(c, r.Cost.Currency);
        }

        // Contingency + grand totals referencing the Total column subtotal.
        int contingencyRow = totalsRow + 2;
        int totalRowNo = contingencyRow + 1;
        int annualRow = totalRowNo + 1;

        ws.Cell(contingencyRow, 1).Value = $"Contingency ({r.Cost.ContingencyPercent}%)";
        ws.Cell(contingencyRow, 1).Style.Font.Bold = true;
        var contingencyCell = ws.Cell(contingencyRow, 2);
        contingencyCell.FormulaA1 = $"={tableName}[[#Totals],[Total Cost]]*{r.Cost.ContingencyPercent / 100m}";
        MoneyCell(contingencyCell, r.Cost.Currency);
        double contingency = totalColSubtotal * (double)(r.Cost.ContingencyPercent / 100m);
        Cache(contingencyCell, contingency);

        ws.Cell(totalRowNo, 1).Value = "Total monthly (incl. contingency)";
        ws.Cell(totalRowNo, 1).Style.Font.Bold = true;
        var totalCell = ws.Cell(totalRowNo, 2);
        totalCell.FormulaA1 = $"={tableName}[[#Totals],[Total Cost]]+B{contingencyRow}";
        MoneyCell(totalCell, r.Cost.Currency);
        Cache(totalCell, totalColSubtotal + contingency);
        ws.Range(totalRowNo, 1, totalRowNo, 2).Style.Fill.BackgroundColor = TotalFill;

        ws.Cell(annualRow, 1).Value = "Total annual (incl. contingency)";
        ws.Cell(annualRow, 1).Style.Font.Bold = true;
        var annualCell = ws.Cell(annualRow, 2);
        annualCell.FormulaA1 = $"=B{totalRowNo}*12";
        MoneyCell(annualCell, r.Cost.Currency);
        Cache(annualCell, (totalColSubtotal + contingency) * 12);
        ws.Range(annualRow, 1, annualRow, 2).Style.Fill.BackgroundColor = TotalFill;

        wb.DefinedNames.Add(NameTotalMonthly, totalSubtotalCell.AsRange());

        double[] widths = { 16, 22, 14, 14, 14, 12, 12, 14, 14, 14, 22 };
        for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ws.SheetView.FreezeRows(headerRow);
    }

    /// <summary>
    /// Builds the one-time delivery cost sheet: delivery roles with an editable Day Rate and Estimated
    /// Days, a per-row Cost = Day Rate * Estimated Days A1 formula, a SUBTOTAL totals row that auto-includes
    /// inserted role rows, and contingency / total-with-contingency below the table. Same editable-table
    /// pattern (and ClosedXML cached-value handling) as the Cost Model sheet.
    /// </summary>
    private void BuildProjectCostSheet(XLWorkbook wb, EstimationResult r)
    {
        const string tableName = "ProjectCost";
        var ws = wb.Worksheets.Add("Project Cost");
        Title(ws, "A1:E1", "Project build cost — delivery team (one-time)");
        ws.Cell("A2").Value =
            "One-time cost to design and build the solution. Edit the highlighted Day Rate / Estimated Days " +
            "cells and Cost recalculates automatically. To add a role, type in the empty row directly below " +
            "the last one — the table grows and the Labour subtotal includes it. Do not delete the totals row.";
        ws.Range("A2:E2").Merge();
        ws.Cell("A2").Style.Alignment.WrapText = true;
        ws.Cell("A2").Style.Font.Italic = true;
        ws.Cell("A2").Style.Font.FontColor = XLColor.FromHtml("#444444");
        ws.Range("A2:E2").Style.Fill.BackgroundColor = AccentFill;
        ws.Row(2).Height = 42;

        string[] headers = { "Role", "Description", "Day Rate", "Estimated Days", "Cost" };
        const int colDayRate = 3, colDays = 4, colCost = 5;
        int headerRow = 4;
        for (int c = 0; c < headers.Length; c++) { var cell = ws.Cell(headerRow, c + 1); cell.Value = headers[c]; HeaderStyle(cell); }

        int row = headerRow + 1;
        int firstDataRow = row;
        var roles = r.ProjectCost.Roles;
        if (roles.Count == 0) roles = new List<ProjectRoleLineItem> { new() { Role = "" } };
        foreach (var role in roles)
        {
            ws.Cell(row, 1).Value = role.Role;
            ws.Cell(row, 2).Value = role.Description;
            ws.Cell(row, colDayRate).Value = role.DayRate;       // editable input
            ws.Cell(row, colDays).Value = role.EstimatedDays;    // editable input
            row++;
        }
        int lastDataRow = row - 1;

        var table = ws.Range(headerRow, 1, lastDataRow, headers.Length).CreateTable(tableName);
        table.Theme = XLTableTheme.TableStyleLight12;
        double subtotal = 0;
        foreach (var dataRow in table.DataRange.Rows())
        {
            var costCell = dataRow.Cell(colCost);
            int rowNum = costCell.Address.RowNumber;
            costCell.FormulaA1 = $"={XLHelper.GetColumnLetterFromNumber(colDayRate)}{rowNum}*{XLHelper.GetColumnLetterFromNumber(colDays)}{rowNum}";
            double cost = dataRow.Cell(colDayRate).GetDouble() * dataRow.Cell(colDays).GetDouble();
            Cache(costCell, cost);
            subtotal += cost;
        }

        table.ShowTotalsRow = true;
        table.Field("Role").TotalsRowLabel = "Labour subtotal";
        table.Field("Estimated Days").TotalsRowFunction = XLTotalsRowFunction.Sum;
        table.Field("Cost").TotalsRowFunction = XLTotalsRowFunction.Sum;
        int totalsRow = table.TotalsRow().RowNumber();
        var subtotalCell = ws.Cell(totalsRow, colCost);
        MoneyCell(subtotalCell, r.ProjectCost.Currency);
        Cache(subtotalCell, subtotal);
        Cache(ws.Cell(totalsRow, colDays), (double)r.ProjectCost.TotalDays);
        ws.Cell(totalsRow, 1).Style.Font.Bold = true;

        var rateData = ws.Range(firstDataRow, colDayRate, lastDataRow, colDayRate);
        var daysData = ws.Range(firstDataRow, colDays, lastDataRow, colDays);
        foreach (var c in rateData.Cells()) MoneyCell(c, r.ProjectCost.Currency);
        daysData.Style.NumberFormat.Format = "#,##0.#";
        foreach (var rng in new[] { rateData, daysData })
        {
            rng.Style.Fill.BackgroundColor = InputFill;
            rng.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.OutsideBorderColor = InputBorder;
        }
        var costData = ws.Range(firstDataRow, colCost, lastDataRow, colCost);
        costData.Style.Font.Italic = true;
        foreach (var c in costData.Cells()) MoneyCell(c, r.ProjectCost.Currency);

        int contingencyRow = totalsRow + 2;
        int totalRowNo = contingencyRow + 1;

        ws.Cell(contingencyRow, 1).Value = $"Contingency ({r.ProjectCost.ContingencyPercent}%)";
        ws.Cell(contingencyRow, 1).Style.Font.Bold = true;
        var contingencyCell = ws.Cell(contingencyRow, 2);
        contingencyCell.FormulaA1 = $"={tableName}[[#Totals],[Cost]]*{r.ProjectCost.ContingencyPercent / 100m}";
        MoneyCell(contingencyCell, r.ProjectCost.Currency);
        double contingency = subtotal * (double)(r.ProjectCost.ContingencyPercent / 100m);
        Cache(contingencyCell, contingency);

        ws.Cell(totalRowNo, 1).Value = "Total build cost (incl. contingency)";
        ws.Cell(totalRowNo, 1).Style.Font.Bold = true;
        var totalCell = ws.Cell(totalRowNo, 2);
        totalCell.FormulaA1 = $"={tableName}[[#Totals],[Cost]]+B{contingencyRow}";
        MoneyCell(totalCell, r.ProjectCost.Currency);
        Cache(totalCell, subtotal + contingency);
        ws.Range(totalRowNo, 1, totalRowNo, 2).Style.Fill.BackgroundColor = TotalFill;

        int noteRow = totalRowNo + 2;
        ws.Cell(noteRow, 1).Value = "Notes"; ws.Cell(noteRow, 1).Style.Font.Bold = true;
        foreach (var note in r.ProjectCost.Notes) { noteRow++; ws.Cell(noteRow, 1).Value = "• " + note; }

        double[] widths = { 24, 50, 14, 16, 16 };
        for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ws.SheetView.FreezeRows(headerRow);
    }

    /// <summary>
    /// Builds the ongoing run/maintain cost sheet: operating line items with an editable Quantity and
    /// Unit Price, a per-row Monthly Cost = Quantity * Unit Price A1 formula, a SUBTOTAL totals row, and
    /// contingency / monthly / annual totals below the table. Same editable-table pattern as Cost Model.
    /// </summary>
    private void BuildOperationCostSheet(XLWorkbook wb, EstimationResult r)
    {
        const string tableName = "OperationCost";
        var ws = wb.Worksheets.Add("Operation Cost");
        Title(ws, "A1:G1", "Operation cost — run & maintain (ongoing, monthly)");
        ws.Cell("A2").Value =
            "Ongoing monthly cost to run, support, and maintain the solution after go-live (excludes Azure infra). " +
            "Edit the highlighted Quantity / Unit Price cells and Monthly Cost recalculates automatically. To add a " +
            "line item, type in the empty row directly below the last one. Do not delete the totals row.";
        ws.Range("A2:G2").Merge();
        ws.Cell("A2").Style.Alignment.WrapText = true;
        ws.Cell("A2").Style.Font.Italic = true;
        ws.Cell("A2").Style.Font.FontColor = XLColor.FromHtml("#444444");
        ws.Range("A2:G2").Style.Fill.BackgroundColor = AccentFill;
        ws.Row(2).Height = 42;

        string[] headers = { "Category", "Item", "Cadence", "Quantity", "Unit Price", "Unit", "Monthly Cost" };
        const int colQty = 4, colUnitPrice = 5, colMonthly = 7;
        int headerRow = 4;
        for (int c = 0; c < headers.Length; c++) { var cell = ws.Cell(headerRow, c + 1); cell.Value = headers[c]; HeaderStyle(cell); }

        int row = headerRow + 1;
        int firstDataRow = row;
        var items = r.Operations.Items;
        if (items.Count == 0) items = new List<OperationCostLineItem> { new() { Item = "" } };
        foreach (var it in items)
        {
            ws.Cell(row, 1).Value = it.Category;
            ws.Cell(row, 2).Value = it.Item;
            ws.Cell(row, 3).Value = it.Cadence;
            ws.Cell(row, colQty).Value = it.Quantity;            // editable input
            ws.Cell(row, colUnitPrice).Value = it.UnitPrice;     // editable input
            ws.Cell(row, 6).Value = it.Unit;
            row++;
        }
        int lastDataRow = row - 1;

        var table = ws.Range(headerRow, 1, lastDataRow, headers.Length).CreateTable(tableName);
        table.Theme = XLTableTheme.TableStyleLight14;
        double subtotal = 0;
        foreach (var dataRow in table.DataRange.Rows())
        {
            var monthlyCell = dataRow.Cell(colMonthly);
            int rowNum = monthlyCell.Address.RowNumber;
            monthlyCell.FormulaA1 = $"={XLHelper.GetColumnLetterFromNumber(colQty)}{rowNum}*{XLHelper.GetColumnLetterFromNumber(colUnitPrice)}{rowNum}";
            double monthly = dataRow.Cell(colQty).GetDouble() * dataRow.Cell(colUnitPrice).GetDouble();
            Cache(monthlyCell, monthly);
            subtotal += monthly;
        }

        table.ShowTotalsRow = true;
        table.Field("Category").TotalsRowLabel = "Monthly subtotal";
        table.Field("Monthly Cost").TotalsRowFunction = XLTotalsRowFunction.Sum;
        int totalsRow = table.TotalsRow().RowNumber();
        var subtotalCell = ws.Cell(totalsRow, colMonthly);
        MoneyCell(subtotalCell, r.Operations.Currency);
        Cache(subtotalCell, subtotal);
        ws.Cell(totalsRow, 1).Style.Font.Bold = true;

        var qtyData = ws.Range(firstDataRow, colQty, lastDataRow, colQty);
        var priceData = ws.Range(firstDataRow, colUnitPrice, lastDataRow, colUnitPrice);
        qtyData.Style.NumberFormat.Format = "#,##0.######";
        foreach (var c in priceData.Cells()) MoneyCell(c, r.Operations.Currency);
        foreach (var rng in new[] { qtyData, priceData })
        {
            rng.Style.Fill.BackgroundColor = InputFill;
            rng.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.OutsideBorderColor = InputBorder;
        }
        var monthlyData = ws.Range(firstDataRow, colMonthly, lastDataRow, colMonthly);
        monthlyData.Style.Font.Italic = true;
        foreach (var c in monthlyData.Cells()) MoneyCell(c, r.Operations.Currency);

        int contingencyRow = totalsRow + 2;
        int totalRowNo = contingencyRow + 1;
        int annualRow = totalRowNo + 1;

        ws.Cell(contingencyRow, 1).Value = $"Contingency ({r.Operations.ContingencyPercent}%)";
        ws.Cell(contingencyRow, 1).Style.Font.Bold = true;
        var contingencyCell = ws.Cell(contingencyRow, 2);
        contingencyCell.FormulaA1 = $"={tableName}[[#Totals],[Monthly Cost]]*{r.Operations.ContingencyPercent / 100m}";
        MoneyCell(contingencyCell, r.Operations.Currency);
        double contingency = subtotal * (double)(r.Operations.ContingencyPercent / 100m);
        Cache(contingencyCell, contingency);

        ws.Cell(totalRowNo, 1).Value = "Monthly total (incl. contingency)";
        ws.Cell(totalRowNo, 1).Style.Font.Bold = true;
        var totalCell = ws.Cell(totalRowNo, 2);
        totalCell.FormulaA1 = $"={tableName}[[#Totals],[Monthly Cost]]+B{contingencyRow}";
        MoneyCell(totalCell, r.Operations.Currency);
        Cache(totalCell, subtotal + contingency);
        ws.Range(totalRowNo, 1, totalRowNo, 2).Style.Fill.BackgroundColor = TotalFill;

        ws.Cell(annualRow, 1).Value = "Annual total (incl. contingency)";
        ws.Cell(annualRow, 1).Style.Font.Bold = true;
        var annualCell = ws.Cell(annualRow, 2);
        annualCell.FormulaA1 = $"=B{totalRowNo}*12";
        MoneyCell(annualCell, r.Operations.Currency);
        Cache(annualCell, (subtotal + contingency) * 12);
        ws.Range(annualRow, 1, annualRow, 2).Style.Fill.BackgroundColor = TotalFill;

        int noteRow = annualRow + 2;
        ws.Cell(noteRow, 1).Value = "Notes"; ws.Cell(noteRow, 1).Style.Font.Bold = true;
        foreach (var note in r.Operations.Notes) { noteRow++; ws.Cell(noteRow, 1).Value = "• " + note; }

        double[] widths = { 16, 34, 12, 12, 14, 12, 16 };
        for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ws.SheetView.FreezeRows(headerRow);
    }

    /// <summary>Writes a first-party Azure pricing reference into a cell as a friendly hyperlink.</summary>
    private static void WritePricingRef(IXLCell cell, CostLineItem li)
    {
        if (string.IsNullOrWhiteSpace(li.PricingReferenceUrl))
        {
            cell.Value = "—";
            return;
        }
        cell.Value = string.IsNullOrWhiteSpace(li.PricingReferenceLabel) ? "Azure pricing" : li.PricingReferenceLabel;
        cell.SetHyperlink(new XLHyperlink(li.PricingReferenceUrl));
        cell.Style.Font.FontColor = XLColor.FromHtml("#1A73E8");
        cell.Style.Font.Underline = XLFontUnderlineValues.Single;
    }

    private static void BuildRequirementsSheet(XLWorkbook wb, EstimationResult r)
    {
        var ws = wb.Worksheets.Add("Requirements");
        string[] headers = { "ID", "Category", "Priority", "Requirement", "Rationale" };
        for (int c = 0; c < headers.Length; c++) { var cell = ws.Cell(1, c + 1); cell.Value = headers[c]; HeaderStyle(cell); }

        int row = 2;
        foreach (var req in r.Requirements)
        {
            ws.Cell(row, 1).Value = req.Id;
            ws.Cell(row, 2).Value = req.Category;
            ws.Cell(row, 3).Value = req.Priority;
            ws.Cell(row, 4).Value = req.Requirement;
            ws.Cell(row, 5).Value = req.Rationale;
            if (row % 2 == 0) ws.Range(row, 1, row, 5).Style.Fill.BackgroundColor = AccentFill;
            row++;
        }
        ws.Columns(1, 5).AdjustToContents();
        ws.Column(4).Width = Math.Min(Math.Max(ws.Column(4).Width, 40), 70);
        ws.Column(5).Width = Math.Min(Math.Max(ws.Column(5).Width, 40), 70);
        ws.Column(4).Style.Alignment.WrapText = true;
        ws.Column(5).Style.Alignment.WrapText = true;
        ws.SheetView.FreezeRows(1);
        if (row > 2) ws.Range(1, 1, row - 1, 5).SetAutoFilter();
    }

    private static void BuildScopeSheet(XLWorkbook wb, EstimationResult r)
    {
        var ws = wb.Worksheets.Add("Scope");
        ws.Column(1).Width = 22; ws.Column(2).Width = 80;
        Title(ws, "A1:B1", "Scope Summary");

        int row = 3;
        void Field(string k, string v) { ws.Cell(row, 1).Value = k; ws.Cell(row, 1).Style.Font.Bold = true; ws.Cell(row, 2).Value = v; ws.Cell(row, 2).Style.Alignment.WrapText = true; row++; }
        void ListBlock(string k, List<string> items)
        {
            ws.Cell(row, 1).Value = k; ws.Cell(row, 1).Style.Font.Bold = true;
            if (items.Count == 0) { ws.Cell(row, 2).Value = "—"; row++; return; }
            foreach (var it in items) { ws.Cell(row, 2).Value = "• " + it; row++; }
            row++;
        }

        Field("Project", r.Scope.ProjectName);
        Field("Overview", r.Scope.Overview);
        Field("Business goal", r.Scope.BusinessGoal);
        Field("Workload profile", r.Scope.WorkloadProfile);
        Field("Expected scale", r.Scope.ExpectedScale);
        Field("Data sensitivity", r.Scope.DataSensitivity);
        Field("Environment", r.Scope.Environment);
        row++;
        ListBlock("In scope", r.Scope.InScope);
        ListBlock("Out of scope", r.Scope.OutOfScope);
        ListBlock("Assumptions", r.Scope.Assumptions);
    }

    private static void BuildDocumentsSheet(XLWorkbook wb, EstimationResult r)
    {
        var ws = wb.Worksheets.Add("Documents");
        string[] headers = { "File", "Content Type", "Size (bytes)", "Words", "Characters", "Excerpt" };
        for (int c = 0; c < headers.Length; c++) { var cell = ws.Cell(1, c + 1); cell.Value = headers[c]; HeaderStyle(cell); }
        int row = 2;
        foreach (var d in r.Documents)
        {
            ws.Cell(row, 1).Value = d.FileName;
            ws.Cell(row, 2).Value = d.ContentType;
            ws.Cell(row, 3).Value = d.SizeBytes;
            ws.Cell(row, 4).Value = d.WordCount;
            ws.Cell(row, 5).Value = d.CharacterCount;
            ws.Cell(row, 6).Value = d.Excerpt ?? "";
            row++;
        }
        ws.Columns(1, 6).AdjustToContents();
        ws.Column(6).Width = Math.Min(Math.Max(ws.Column(6).Width, 40), 80);
        ws.Column(6).Style.Alignment.WrapText = true;
        ws.SheetView.FreezeRows(1);
    }

    // ------- styling helpers -------
    private static void Title(IXLWorksheet ws, string range, string text)
    {
        ws.Range(range).Merge();
        var c = ws.Range(range).FirstCell();
        c.Value = text;
        c.Style.Font.Bold = true;
        c.Style.Font.FontSize = 16;
        c.Style.Font.FontColor = HeaderText;
        ws.Range(range).Style.Fill.BackgroundColor = HeaderFill;
        ws.Range(range).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(ws.Range(range).FirstCell().Address.RowNumber).Height = 26;
    }

    private static void SectionHeader(IXLWorksheet ws, string range, string text)
    {
        ws.Range(range).Merge();
        var c = ws.Range(range).FirstCell();
        c.Value = text;
        c.Style.Font.Bold = true;
        c.Style.Font.FontColor = HeaderText;
        ws.Range(range).Style.Fill.BackgroundColor = HeaderFill;
    }

    private static void HeaderStyle(IXLCell cell)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = HeaderText;
        cell.Style.Fill.BackgroundColor = HeaderFill;
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void MoneyCell(IXLCell cell, string currency, string format = "#,##0.00")
    {
        cell.Style.NumberFormat.Format = $"\"{CurrencySymbol(currency)}\"{format}";
    }

    private static string CurrencySymbol(string currency) => currency.ToUpperInvariant() switch
    {
        "USD" => "$",
        "AUD" => "A$",
        "EUR" => "€",
        "GBP" => "£",
        _ => currency + " "
    };
}
