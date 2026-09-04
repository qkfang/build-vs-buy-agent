using System.Text.Json;
using System.Text.RegularExpressions;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Enumerates and serves the bundled mock vendor documents (JSON) that ship under
/// <c>Data/vendor-docs/</c>. These are surfaced on the Spec (Buy tab) page as a dropdown of
/// off-the-shelf "vendor spec + pricing" documents that can be loaded in place of an upload, so the
/// Spec/Purchase/Buy-operations agents have something concrete to summarise without requiring the
/// user to author their own vendor documents first.
/// </summary>
public sealed partial class VendorDocsService
{
    private readonly string _dir;

    public VendorDocsService(IWebHostEnvironment env)
    {
        _dir = Path.Combine(env.ContentRootPath, "Data", "vendor-docs");
    }

    public sealed record VendorDoc(string Id, string VendorName, string Category, string FileName, int SizeBytes);

    /// <summary>Lists the mock vendor docs (ordered by file name), with vendor name/category read from the JSON.</summary>
    public IReadOnlyList<VendorDoc> List()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<VendorDoc>();
        var docs = new List<VendorDoc>();
        foreach (var path in Directory.EnumerateFiles(_dir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            var id = Path.GetFileNameWithoutExtension(fileName);
            string vendorName = Prettify(id);
            string category = "Vendor product";
            long size = 0;
            try
            {
                var info = new FileInfo(path);
                size = info.Length;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("vendorName", out var vn) && vn.ValueKind == JsonValueKind.String)
                    vendorName = vn.GetString() ?? vendorName;
                if (doc.RootElement.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String)
                    category = cat.GetString() ?? category;
            }
            catch { /* fall back to id-derived name */ }
            docs.Add(new VendorDoc(id, vendorName, category, fileName, (int)size));
        }
        return docs;
    }

    /// <summary>Returns the raw JSON for a mock vendor doc id, or null if not found. Path-traversal safe.</summary>
    public string? Read(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !SafeIdRegex().IsMatch(id)) return null;
        var path = Path.GetFullPath(Path.Combine(_dir, id + ".json"));
        var root = Path.GetFullPath(_dir);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string Prettify(string id)
    {
        var s = id.Replace('-', ' ').Replace('_', ' ').Trim();
        return s.Length == 0 ? id : char.ToUpperInvariant(s[0]) + s[1..];
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex SafeIdRegex();
}
