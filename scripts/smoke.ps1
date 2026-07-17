<#
  smoke.ps1 — proj37 Foundry Cost Estimator POC local smoke test.

  Builds the app, starts it on a free port, exercises the API end-to-end (health, sample estimation,
  workbook download + structural validation, unsupported-file rejection), then shuts down.
  Exit code 0 = all checks passed.

  Usage:  pwsh scripts/smoke.ps1
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$webDir = Join-Path $here '..\apps\web'
$port = 5219
$baseUrl = "http://localhost:$port"
$failures = @()

function Check($name, [scriptblock]$test) {
  try {
    & $test
    Write-Host "  [PASS] $name" -ForegroundColor Green
  } catch {
    Write-Host "  [FAIL] $name -> $($_.Exception.Message)" -ForegroundColor Red
    $script:failures += $name
  }
}

Write-Host "==> Building..." -ForegroundColor Cyan
dotnet build (Join-Path $webDir 'Proj37.CostEstimator.Web.csproj') -c Release | Out-Null

Write-Host "==> Starting app on $baseUrl ..." -ForegroundColor Cyan
$proc = Start-Process -FilePath 'dotnet' `
  -ArgumentList @('run', '-c', 'Release', '--no-build', '--project', (Resolve-Path (Join-Path $webDir 'Proj37.CostEstimator.Web.csproj')).Path) `
  -PassThru -WindowStyle Hidden -Environment @{ ASPNETCORE_URLS = $baseUrl; ASPNETCORE_ENVIRONMENT = 'Development' }

try {
  # Wait for readiness
  $ready = $false
  for ($i = 0; $i -lt 30; $i++) {
    try { if ((Invoke-WebRequest "$baseUrl/api/health" -TimeoutSec 3).StatusCode -eq 200) { $ready = $true; break } } catch { }
    Start-Sleep -Milliseconds 700
  }
  if (-not $ready) { throw "App did not become ready at $baseUrl" }

  Write-Host "==> Running checks..." -ForegroundColor Cyan

  Check "GET /api/health returns healthy" {
    $h = Invoke-RestMethod "$baseUrl/api/health"
    if ($h.status -ne 'healthy') { throw "status=$($h.status)" }
  }

  $script:job = $null
  Check "POST /api/estimations/sample completes" {
    $script:job = Invoke-RestMethod -Method Post "$baseUrl/api/estimations/sample"
    if ($script:job.status -ne 'completed') { throw "status=$($script:job.status)" }
    if ($script:job.requirements.Count -lt 5) { throw "too few requirements: $($script:job.requirements.Count)" }
    if ($script:job.cost.lineItems.Count -lt 5) { throw "too few cost items: $($script:job.cost.lineItems.Count)" }
    if ($script:job.cost.monthlyTotalWithContingency -le 0) { throw "monthly total not positive" }
  }

  Check "Workbook downloads and is a valid multi-sheet xlsx" {
    $tmp = Join-Path $env:TEMP "proj37-smoke.xlsx"
    Invoke-WebRequest "$baseUrl/api/estimations/$($script:job.jobId)/workbook" -OutFile $tmp
    if ((Get-Item $tmp).Length -lt 4000) { throw "workbook too small" }
    $extract = Join-Path $env:TEMP "proj37-smoke-xlsx"
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($tmp, $extract)
    $wbXml = Get-Content (Join-Path $extract 'xl\workbook.xml') -Raw
    foreach ($sheet in @('Summary','Cost Model','Requirements','Scope','Documents')) {
      if ($wbXml -notmatch [regex]::Escape($sheet)) { throw "missing sheet: $sheet" }
    }
    # Formulas must be live in the sheet XML (the authoritative source of formulas). We intentionally
    # DROP calcChain.xml so Excel rebuilds it cleanly (a stale calcChain over uncached ClosedXML
    # formulas is a repair trigger), so assert formulas + recalc-on-load instead of calcChain presence.
    $costSheetXml = Get-ChildItem (Join-Path $extract 'xl\worksheets') -Filter *.xml |
      ForEach-Object { Get-Content $_.FullName -Raw } | Out-String
    if ($costSheetXml -notmatch '\[@Quantity\]\*\[@\[Unit Price\]\]') { throw 'cost model calc-column formula missing from sheet XML' }
    if ($costSheetXml -notmatch 'SUBTOTAL\(109') { throw 'totals-row SUBTOTAL formula missing from sheet XML' }
    if ($wbXml -notmatch 'fullCalcOnLoad="1"') { throw 'workbook is missing fullCalcOnLoad (Excel would show blank/missing formula values + repair prompt)' }
    if (Test-Path (Join-Path $extract 'xl\calcChain.xml')) { throw 'calcChain.xml should be removed so Excel rebuilds it (avoids repair prompt)' }
  }

  Check "Workbook has Non-Prod / Prod / Total environment sheets" {
    $extract = Join-Path $env:TEMP "proj37-smoke-xlsx"
    $wbXml = Get-Content (Join-Path $extract 'xl\workbook.xml') -Raw
    foreach ($sheet in @('Non-Prod','Prod','Total')) {
      if ($wbXml -notmatch [regex]::Escape($sheet)) { throw "missing env sheet: $sheet" }
    }
  }

  Check "Workbook cost sheets carry Azure pricing hyperlinks" {
    $extract = Join-Path $env:TEMP "proj37-smoke-xlsx"
    $relsDir = Join-Path $extract 'xl\worksheets\_rels'
    if (-not (Test-Path $relsDir)) { throw "no worksheet rels (no hyperlinks)" }
    $hasAzure = $false
    Get-ChildItem $relsDir -Filter *.rels | ForEach-Object {
      if ((Get-Content $_.FullName -Raw) -match 'azure\.microsoft\.com/pricing') { $hasAzure = $true }
    }
    if (-not $hasAzure) { throw "no azure.microsoft.com pricing hyperlinks found" }
  }

  Check "Estimation exposes non-prod / prod / total cost rollups + pricing refs" {
    $c = $script:job.cost
    if ($c.nonProdMonthlyTotal -le 0) { throw "nonProdMonthlyTotal not positive" }
    if ($c.prodMonthlyTotal -le 0) { throw "prodMonthlyTotal not positive" }
    if ([math]::Round($c.combinedMonthlyTotal,2) -lt [math]::Round($c.prodMonthlyTotal,2)) { throw "combined < prod" }
    $missingRef = ($c.lineItems | Where-Object { -not $_.pricingReferenceUrl }).Count
    if ($missingRef -gt 0) { throw "$missingRef line item(s) missing a pricing reference" }
  }

  Check "Sample doc renders as HTML (Markdown library)" {
    $samples = Invoke-RestMethod "$baseUrl/api/samples"
    if ($samples.Count -lt 1) { throw "no samples" }
    $id = $samples[0].id
    $html = (Invoke-WebRequest "$baseUrl/api/samples/$id/html").Content
    if ($html -notmatch '<h[1-3]') { throw "no rendered headings in sample HTML" }
    if ($html -match '<script>') { throw "raw <script> leaked (XSS not disabled)" }
  }

  Check "Unsupported-only upload returns 422" {
    $png = Join-Path $env:TEMP "proj37-smoke.png"
    [System.IO.File]::WriteAllBytes($png, [byte[]](1..16))
    $r = Invoke-WebRequest -Method Post "$baseUrl/api/estimations" -Form @{ files = Get-Item $png } -SkipHttpErrorCheck
    if ($r.StatusCode -ne 422) { throw "expected 422, got $($r.StatusCode)" }
  }

  Check "GET /api/estimations lists the job" {
    $list = Invoke-RestMethod "$baseUrl/api/estimations"
    if (($list | Where-Object { $_.jobId -eq $script:job.jobId }).Count -lt 1) { throw "job not in list" }
  }
}
finally {
  Write-Host "==> Stopping app..." -ForegroundColor Cyan
  if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
  # also clean child dotnet/app processes spawned by 'dotnet run'
  Get-CimInstance Win32_Process -Filter "Name='Proj37.CostEstimator.Web.exe'" -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
if ($failures.Count -eq 0) {
  Write-Host "SMOKE TEST PASSED — all checks green." -ForegroundColor Green
  exit 0
} else {
  Write-Host "SMOKE TEST FAILED — $($failures.Count) check(s): $($failures -join ', ')" -ForegroundColor Red
  exit 1
}
