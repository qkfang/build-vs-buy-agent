using System.Collections.Concurrent;
using System.Text.Json;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Orchestrates an estimation job end-to-end: ingest documents, run the selected engine, persist the
/// result + generated Excel workbook to the local data folder, and expose lookup/list/download.
///
/// Persistence is file-based (JSON result + .xlsx) under the configured data folder, which on Azure
/// App Service maps to /home/site/data (durable across restarts). This keeps the POC dependency-free
/// while remaining production-upgradeable to Blob storage.
/// </summary>
public sealed class EstimationJobService
{
    private readonly DocumentIngestionService _ingestion;
    private readonly IEstimationEngine _engine;
    private readonly ExcelReportGenerator _excel;
    private readonly StorageOptions _storage;
    private readonly ILogger<EstimationJobService> _logger;
    private readonly string _dataDir;

    // In-memory index for fast listing (rebuilt from disk on startup).
    private readonly ConcurrentDictionary<string, EstimationResult> _cache = new();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public EstimationJobService(
        DocumentIngestionService ingestion,
        IEstimationEngine engine,
        ExcelReportGenerator excel,
        StorageOptions storage,
        IWebHostEnvironment env,
        ILogger<EstimationJobService> logger)
    {
        _ingestion = ingestion;
        _engine = engine;
        _excel = excel;
        _storage = storage;
        _logger = logger;

        _dataDir = Path.IsPathRooted(_storage.LocalDataFolder)
            ? _storage.LocalDataFolder
            : Path.Combine(env.ContentRootPath, _storage.LocalDataFolder);
        Directory.CreateDirectory(_dataDir);
        LoadExisting();
    }

    public sealed record UploadedFile(string FileName, string ContentType, Stream Content);

    public async Task<EstimationResult> CreateAndRunAsync(IEnumerable<UploadedFile> files, CancellationToken ct = default)
    {
        var job = new EstimationResult { Status = "running" };

        foreach (var f in files)
        {
            if (!_ingestion.IsSupported(f.FileName))
            {
                _logger.LogWarning("Skipping unsupported file {File}", f.FileName);
                continue;
            }
            var doc = await _ingestion.IngestAsync(f.FileName, f.ContentType, f.Content, ct);
            job.Documents.Add(doc);
        }

        if (job.Documents.Count == 0)
        {
            job.Status = "failed";
            job.Error = "No supported documents were provided. Supported: " + string.Join(", ", _ingestion.SupportedExtensions);
            Persist(job);
            return job;
        }

        try
        {
            await _engine.EstimateAsync(job, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Estimation engine threw unexpectedly for job {Job}", job.JobId);
            job.Status = "failed";
            job.Error = ex.Message;
        }

        // Generate + persist the Excel workbook for completed jobs.
        if (job.Status == "completed")
        {
            try
            {
                var bytes = _excel.Generate(job);
                File.WriteAllBytes(WorkbookPath(job.JobId), bytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate workbook for job {Job}", job.JobId);
                job.Error = "Estimation completed but workbook generation failed: " + ex.Message;
            }
        }

        Persist(job);
        return job;
    }

    public EstimationResult? Get(string jobId) => _cache.TryGetValue(jobId, out var r) ? r : null;

    public IReadOnlyList<EstimationResult> List() =>
        _cache.Values.OrderByDescending(r => r.CreatedUtc).ToList();

    public bool TryGetWorkbook(string jobId, out byte[] bytes, out string fileName)
    {
        bytes = Array.Empty<byte>();
        fileName = $"azure-cost-estimate-{jobId}.xlsx";
        var path = WorkbookPath(jobId);
        if (!File.Exists(path)) return false;
        bytes = File.ReadAllBytes(path);
        var job = Get(jobId);
        if (job is not null)
        {
            var safe = string.Concat((job.Scope.ProjectName ?? "estimate").Split(Path.GetInvalidFileNameChars()));
            if (!string.IsNullOrWhiteSpace(safe)) fileName = $"azure-cost-estimate-{safe}.xlsx";
        }
        return true;
    }

    // ---------------- persistence ----------------
    private string ResultPath(string jobId) => Path.Combine(_dataDir, $"{jobId}.json");
    private string WorkbookPath(string jobId) => Path.Combine(_dataDir, $"{jobId}.xlsx");

    private void Persist(EstimationResult job)
    {
        _cache[job.JobId] = job;
        try
        {
            File.WriteAllText(ResultPath(job.JobId), JsonSerializer.Serialize(job, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist job {Job}", job.JobId);
        }
    }

    private void LoadExisting()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dataDir, "*.json"))
            {
                try
                {
                    var job = JsonSerializer.Deserialize<EstimationResult>(File.ReadAllText(file), JsonOpts);
                    if (job is not null) _cache[job.JobId] = job;
                }
                catch { /* skip malformed */ }
            }
            _logger.LogInformation("Loaded {Count} existing estimation job(s) from {Dir}", _cache.Count, _dataDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load existing jobs from {Dir}", _dataDir);
        }
    }
}
