using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Proj37.CostEstimator.Web.Models;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Extracts plain text from uploaded technical documents so the estimation pipeline can ground on
/// real content. Supports the common text-based formats plus DOCX. Designed to be dependency-light
/// and cross-platform (works on Linux App Service).
/// </summary>
public sealed class DocumentIngestionService
{
    private const int MaxExtractedChars = 60_000; // keep prompts bounded

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".tsv", ".log", ".yaml", ".yml",
        ".xml", ".html", ".htm", ".cs", ".js", ".ts", ".py", ".sql"
    };

    public bool IsSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return TextExtensions.Contains(ext) || ext.Equals(".docx", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> SupportedExtensions =>
        TextExtensions.Append(".docx").OrderBy(x => x).ToArray();

    public async Task<IngestedDocument> IngestAsync(string fileName, string contentType, Stream content, CancellationToken ct = default)
    {
        // Buffer to memory so we can both measure size and parse.
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var text = ExtractText(fileName, bytes);
        if (text.Length > MaxExtractedChars)
        {
            text = text[..MaxExtractedChars] + "\n\n[...truncated for length...]";
        }

        var wordCount = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

        return new IngestedDocument
        {
            FileName = fileName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            SizeBytes = bytes.LongLength,
            CharacterCount = text.Length,
            WordCount = wordCount,
            ExtractedText = text,
            Excerpt = BuildExcerpt(text)
        };
    }

    private static string ExtractText(string fileName, byte[] bytes)
    {
        var ext = Path.GetExtension(fileName);
        try
        {
            if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractDocx(bytes);
            }
            // Default: decode as UTF-8 text (handles md/json/csv/txt/etc.)
            return DecodeText(bytes);
        }
        catch (Exception ex)
        {
            return $"[Unable to extract text from {fileName}: {ex.Message}]";
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        // Strip a UTF-8 BOM if present.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string ExtractDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var para in body.Descendants<Paragraph>())
        {
            var line = string.Concat(para.Descendants<Text>().Select(t => t.Text));
            if (!string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    private static string BuildExcerpt(string text, int max = 280)
    {
        var trimmed = text.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }
}
