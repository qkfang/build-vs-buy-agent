using System.Text.RegularExpressions;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Extracts workload signals from ingested document text using keyword heuristics, then derives
/// sizing parameters (instances, token volumes, storage, etc.). This is the deterministic
/// "understanding" layer for the offline engine and provides sane defaults the Foundry agent can
/// also lean on. All thresholds are documented in docs/solution.md.
/// </summary>
public sealed partial class WorkloadSignals
{
    // Capability flags
    public bool HasWebApp { get; private set; } = true;   // default: assume a web front end for a web-app POC
    public bool HasApi { get; private set; }
    public bool HasAi { get; private set; }
    public bool HasFileSearch { get; private set; }
    public bool HasRelationalDb { get; private set; }
    public bool HasNoSql { get; private set; }
    public bool HasFunctions { get; private set; }
    public bool IsProduction { get; private set; } = true;

    // Scale band: 1 = small/POC, 2 = medium, 3 = large/enterprise
    public int ScaleBand { get; private set; } = 1;
    public bool MentionsPii { get; private set; }
    public bool MentionsRegulated { get; private set; }

    // Derived sizing
    public string RecommendedAppServiceSku => ScaleBand switch { >= 3 => "P2v3", 2 => "P1v3", _ => "B2" };
    public int AppServiceInstances => ScaleBand switch { >= 3 => 3, 2 => 2, _ => 1 };
    public string PreferredModel { get; private set; } = "gpt-4o";

    public int MonthlyAiRequests => ScaleBand switch { >= 3 => 200_000, 2 => 40_000, _ => 5_000 };
    public int AvgInputTokens => HasFileSearch ? 4_000 : 1_500;
    public int AvgOutputTokens => 1_200;

    public int StorageGb => ScaleBand switch { >= 3 => 500, 2 => 100, _ => 20 };
    public int LogGbPerMonth => ScaleBand switch { >= 3 => 50, 2 => 15, _ => 5 };
    public int KeyVaultOps10K => ScaleBand switch { >= 3 => 200, 2 => 50, _ => 10 };
    public int EgressGb => ScaleBand switch { >= 3 => 500, 2 => 100, _ => 25 };
    public int MonthlyCosmosMillionRu => ScaleBand switch { >= 3 => 50, 2 => 10, _ => 2 };
    public decimal MonthlyFunctionMillions => ScaleBand switch { >= 3 => 10m, 2 => 2m, _ => 0.5m };

    public decimal ContingencyPercent => ScaleBand >= 3 ? 25m : 20m;

    public string DescribeScale() => ScaleBand switch
    {
        >= 3 => "Large / enterprise (≈200k AI req/mo, multi-instance compute)",
        2 => "Medium (≈40k AI req/mo, 2 instances)",
        _ => "Small / POC (≈5k AI req/mo, single instance)"
    };

    public string DescribeDataSensitivity()
    {
        if (MentionsRegulated) return "regulated (e.g. PCI/HIPAA-class) — apply strict controls";
        if (MentionsPii) return "contains PII — restricted access + retention controls";
        return "internal business data";
    }

    public static WorkloadSignals FromText(string text)
    {
        var s = new WorkloadSignals();
        var t = text.ToLowerInvariant();

        bool Has(params string[] words) => words.Any(w => t.Contains(w));
        // Word-boundary match for short/ambiguous tokens (e.g. "ai" must not match "Tailwind"/"email").
        bool HasWord(params string[] words) => words.Any(w => Regex.IsMatch(t, $@"\b{Regex.Escape(w)}\b"));

        // Capability detection
        s.HasApi = Has("rest", "endpoint", "openapi", "swagger", "microservice", "integration") || HasWord("api");
        s.HasAi = Has("llm", "gpt", "openai", "foundry", "prompt", "embedding", "generative", "large language model", "language model") || HasWord("ai", "agent", "rag");
        s.HasFileSearch = Has("file search", "vector", "knowledge base", "grounding", "retrieval-augmented", "retrieval augmented", "document understanding", "file upload") || HasWord("rag");
        s.HasRelationalDb = Has("sql", "relational", "postgres", "mysql", "transaction", "database table", "ef core", "entity framework");
        s.HasNoSql = Has("cosmos", "nosql", "mongo", "document database", "key-value", "key value");
        s.HasFunctions = Has("function", "serverless", "event-driven", "queue", "background job", "webhook", "trigger");

        if (Has("web app", "website", "front end", "frontend", "portal", "ui", "blazor", "react", "angular", "spa"))
            s.HasWebApp = true;

        // Environment
        if (Has("poc", "proof of concept", "prototype", "demo", "pilot", "sandbox", "non-production", "dev environment"))
            s.IsProduction = false;

        // Scale band
        int scaleScore = 0;
        if (Has("enterprise", "large scale", "high availability", "global", "millions", "high throughput", "mission critical", "100k", "1m"))
            scaleScore += 2;
        if (Has("scale", "concurrent", "peak", "thousands", "10k", "growth", "multi-region", "multi region"))
            scaleScore += 1;
        // Explicit user counts
        var users = ExtractMaxNumberNear(t, UsersRegex());
        if (users >= 100_000) scaleScore += 2;
        else if (users >= 5_000) scaleScore += 1;

        s.ScaleBand = scaleScore switch { >= 3 => 3, >= 1 => 2, _ => 1 };

        // Data sensitivity
        s.MentionsPii = Has("pii", "personal data", "personally identifiable", "gdpr", "customer data", "sensitive");
        s.MentionsRegulated = Has("hipaa", "pci", "regulated", "compliance", "soc 2", "soc2", "iso 27001", "phi", "financial data");

        // Model preference
        if (Has("gpt-5", "gpt5", "flagship", "advanced reasoning", "complex reasoning"))
            s.PreferredModel = "gpt-5.4";
        else if (Has("cheap", "low cost", "cost-effective", "mini", "lightweight"))
            s.PreferredModel = "gpt-4o-mini";
        else
            s.PreferredModel = "gpt-4o";

        return s;
    }

    private static int ExtractMaxNumberNear(string text, Regex rx)
    {
        int max = 0;
        foreach (Match m in rx.Matches(text))
        {
            var raw = m.Groups[1].Value.Replace(",", "").Replace(" ", "");
            decimal mult = 1;
            if (raw.EndsWith('k')) { mult = 1_000; raw = raw[..^1]; }
            else if (raw.EndsWith('m')) { mult = 1_000_000; raw = raw[..^1]; }
            if (decimal.TryParse(raw, out var val))
            {
                var n = (int)Math.Min(int.MaxValue, val * mult);
                if (n > max) max = n;
            }
        }
        return max;
    }

    [GeneratedRegex(@"([\d][\d,\.]*\s?[kmKM]?)\s*(?:users|mau|customers|requests|req/s|rps|concurrent)")]
    private static partial Regex UsersRegex();
}
