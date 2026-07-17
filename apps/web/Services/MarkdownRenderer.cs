using Markdig;

namespace Proj37.CostEstimator.Web.Services;

/// <summary>
/// Renders trusted/untrusted markdown into HTML for the in-app document viewer (the Upload-page
/// "View" popup). Uses <see href="https://github.com/xoofx/markdig">Markdig</see>, a fast, CommonMark
/// compliant .NET markdown processor.
///
/// Safety: the pipeline calls <c>DisableHtml()</c> so any raw inline/block HTML embedded in a sample
/// document is emitted as escaped text rather than active markup. Combined with the fact that sample
/// content is server-owned markdown, this keeps the rendered popup XSS-safe while still giving us rich
/// headings, lists, tables, code blocks and autolinks instead of a raw <c>&lt;pre&gt;</c> dump.
/// </summary>
public sealed class MarkdownRenderer
{
    // Built once; MarkdownPipeline is immutable and thread-safe, so a singleton is ideal.
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()             // GitHub-style tables
        .UseGridTables()             // grid tables
        .UseEmphasisExtras()         // strikethrough, subscript, etc.
        .UseAutoLinks()              // bare URLs become links
        .UseTaskLists()              // [ ] / [x] checkboxes
        .UseListExtras()
        .UseFootnotes()
        .UseDefinitionLists()
        .UseGenericAttributes()
        .DisableHtml()               // SECURITY: escape any embedded raw HTML instead of trusting it
        .Build();

    /// <summary>Render markdown source to safe HTML. Never throws on bad input; returns escaped text.</summary>
    public string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        return Markdown.ToHtml(markdown, _pipeline);
    }
}
