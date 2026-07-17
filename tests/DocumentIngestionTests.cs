using Xunit;
using System.Text;
using Proj37.CostEstimator.Web.Services;

namespace Proj37.CostEstimator.Tests;

public class DocumentIngestionTests
{
    private static Stream S(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("brief.md", true)]
    [InlineData("notes.txt", true)]
    [InlineData("data.json", true)]
    [InlineData("spec.docx", true)]
    [InlineData("image.png", false)]
    [InlineData("archive.zip", false)]
    public void IsSupported_matches_expected(string file, bool expected)
    {
        var svc = new DocumentIngestionService();
        Assert.Equal(expected, svc.IsSupported(file));
    }

    [Fact]
    public async Task IngestAsync_extracts_text_and_counts()
    {
        var svc = new DocumentIngestionService();
        var doc = await svc.IngestAsync("brief.md", "text/markdown", S("# Title\nHello world from a POC brief."));

        Assert.Equal("brief.md", doc.FileName);
        Assert.Contains("Hello world", doc.ExtractedText);
        Assert.True(doc.WordCount >= 6);
        Assert.True(doc.CharacterCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(doc.Excerpt));
    }

    [Fact]
    public async Task IngestAsync_strips_utf8_bom()
    {
        var svc = new DocumentIngestionService();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("clean")).ToArray();
        var doc = await svc.IngestAsync("x.txt", "text/plain", new MemoryStream(bytes));
        Assert.StartsWith("clean", doc.ExtractedText);
    }

    [Fact]
    public async Task IngestAsync_unsupported_binary_does_not_throw()
    {
        // .docx path will fail to open random bytes but ingestion must not throw — it returns a marker.
        var svc = new DocumentIngestionService();
        var doc = await svc.IngestAsync("bad.docx", "application/octet-stream", S("not a real docx"));
        Assert.NotNull(doc.ExtractedText);
    }
}
