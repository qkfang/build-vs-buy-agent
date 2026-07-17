using System.Text.RegularExpressions;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Enumerates and serves the bundled sample requirement documents (markdown) that ship under
/// <c>Data/requirements/</c>. These are surfaced on the Upload page as clickable examples that
/// open in a modal viewer, and can be submitted directly to the estimation pipeline.
/// </summary>
public sealed partial class SampleRequirementsService
{
    private readonly string _dir;

    public SampleRequirementsService(IWebHostEnvironment env)
    {
        _dir = Path.Combine(env.ContentRootPath, "Data", "requirements");
    }

    public sealed record SampleDoc(string Id, string Title, string FileName, int SizeBytes);

    /// <summary>Lists the sample docs (ordered by file name), with a friendly title from the first H1.</summary>
    public IReadOnlyList<SampleDoc> List()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<SampleDoc>();
        var docs = new List<SampleDoc>();
        foreach (var path in Directory.EnumerateFiles(_dir, "*.md").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            var id = Path.GetFileNameWithoutExtension(fileName);
            string title = id;
            long size = 0;
            try
            {
                var info = new FileInfo(path);
                size = info.Length;
                title = TitleFromMarkdown(File.ReadLines(path)) ?? Prettify(id);
            }
            catch { /* fall back to id */ }
            docs.Add(new SampleDoc(id, title, fileName, (int)size));
        }
        return docs;
    }

    /// <summary>Returns the raw markdown for a sample doc id, or null if not found. Path-traversal safe.</summary>
    public string? Read(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        // Only allow a bare file stem: letters, digits, dash, underscore.
        if (!SafeIdRegex().IsMatch(id)) return null;
        var path = Path.GetFullPath(Path.Combine(_dir, id + ".md"));
        var root = Path.GetFullPath(_dir);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Resolves the absolute path for a sample doc id so it can be fed to the estimator.</summary>
    public string? ResolvePath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !SafeIdRegex().IsMatch(id)) return null;
        var path = Path.GetFullPath(Path.Combine(_dir, id + ".md"));
        var root = Path.GetFullPath(_dir);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(path) ? path : null;
    }

    private static string? TitleFromMarkdown(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var m = HeadingRegex().Match(line);
            if (m.Success)
            {
                var t = m.Groups[1].Value.Trim();
                // Strip a leading "Project:" label for a cleaner card title.
                t = Regex.Replace(t, @"(?i)^project\s*[:\-]\s*", "").Trim();
                if (t.Length > 0) return t;
            }
        }
        return null;
    }

    private static string Prettify(string id)
    {
        var s = Regex.Replace(id, @"^\d+[-_]", "");          // drop leading "01-"
        s = s.Replace('-', ' ').Replace('_', ' ').Trim();
        return s.Length == 0 ? id : char.ToUpperInvariant(s[0]) + s[1..];
    }

    [GeneratedRegex(@"^\s*#\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex SafeIdRegex();
}
