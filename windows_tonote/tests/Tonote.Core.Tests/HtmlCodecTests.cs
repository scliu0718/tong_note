using System;
using System.IO;
using System.Linq;
using Tonote.Core;
using Xunit;

namespace Tonote.Core.Tests;

/// <summary>
/// 批次 1（W-07 ~ W-11）跨端数据契约回归网。
/// 这些用例专门拦住：裸图被清空、表格满宽语义反转、默认色 / 引用灰被固化、
/// 单元格 style 底色被洗掉、内联图片 URI 写进云端 JSON 五类退化。
/// </summary>
public class HtmlCodecTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tonote-html-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── W-07 块级裸 img ──────────────────────────────────────────────

    [Fact]
    public void BareBlockImageIsDecodedAndSurvivesRoundTrip()
    {
        const string html = "<p>文字</p><img src=\"attachments/AAAA/1111.PNG\" width=\"480\">";
        var doc = RichTextHtmlCodec.Decode(html);

        var images = doc.AllRuns().Where(run => run.Image != null).Select(run => run.Image!).ToList();
        Assert.Single(images);
        Assert.Equal("attachments/AAAA/1111.PNG", images[0].Path);
        Assert.Equal(480, images[0].Width);

        var encoded = RichTextHtmlCodec.Encode(doc);
        Assert.Contains("<img", encoded, StringComparison.Ordinal);
        Assert.Contains("attachments/AAAA/1111.PNG", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void BareImageOnlyNoteIsNeverEmpty()
    {
        var doc = RichTextHtmlCodec.Decode("<img src=\"attachments/BBBB/2222.png\">");
        Assert.False(string.IsNullOrWhiteSpace(RichTextHtmlCodec.Encode(doc)));
        Assert.Single(doc.AllRuns().Where(run => run.Image != null).ToList());
    }

    [Fact]
    public void EncodeDecodeEncodeIsStableWithBareImage()
    {
        const string html = "<p>文字</p><img src=\"attachments/CCCC/3333.jpg\">";
        var once = RichTextHtmlCodec.Encode(RichTextHtmlCodec.Decode(html));
        var twice = RichTextHtmlCodec.Encode(RichTextHtmlCodec.Decode(once));
        Assert.Equal(once, twice);
    }

    // ── W-08 表格 data-fullwidth 语义 ────────────────────────────────

    [Fact]
    public void MissingFullWidthMeansFullWidthAndIsNotWritten()
    {
        var doc = RichTextHtmlCodec.Decode("<table><tr><td>A</td></tr></table>");
        var table = Assert.Single(doc.Blocks).Table!;
        Assert.True(table.FullWidth);
        Assert.DoesNotContain("data-fullwidth", RichTextHtmlCodec.Encode(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void FullWidthZeroMeansFixedWidthAndIsPreserved()
    {
        var doc = RichTextHtmlCodec.Decode("<table data-fullwidth=\"0\"><tr><td>A</td></tr></table>");
        var table = Assert.Single(doc.Blocks).Table!;
        Assert.False(table.FullWidth);
        Assert.Contains("data-fullwidth=\"0\"", RichTextHtmlCodec.Encode(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void IllegalFullWidthOneTreatedAsFullWidth()
    {
        var doc = RichTextHtmlCodec.Decode("<table data-fullwidth=\"1\"><tr><td>A</td></tr></table>");
        Assert.True(Assert.Single(doc.Blocks).Table!.FullWidth);
    }

    [Fact]
    public void DataWidthsSurviveRoundTrip()
    {
        const string html = "<table data-widths=\"0.28,0.72\"><tr><td>A</td><td>B</td></tr></table>";
        var encoded = RichTextHtmlCodec.Encode(RichTextHtmlCodec.Decode(html));
        Assert.Contains("data-widths=\"0.28,0.72\"", encoded, StringComparison.Ordinal);
    }

    // ── W-10 ① 颜色契约 ─────────────────────────────────────────────

    [Theory]
    [InlineData("#1D1D1F")]
    [InlineData("#86868B")]
    [InlineData("#1d1d1f")]
    [InlineData("rgb(29,29,31)")]
    public void DecorativeColorsAreNotSerialized(string color)
    {
        var doc = RichTextHtmlCodec.Decode($"<p><span style=\"color:{color}\">正文</span></p>");
        var encoded = RichTextHtmlCodec.Encode(doc);
        Assert.DoesNotContain("color:", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitNonDefaultColorSurvives()
    {
        var doc = RichTextHtmlCodec.Decode("<p><span style=\"color:#E5484D\">正文</span></p>");
        Assert.Contains("color:#E5484D", RichTextHtmlCodec.Encode(doc), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransparentBackgroundIsNotSerialized()
    {
        var doc = RichTextHtmlCodec.Decode("<p><span style=\"background-color:rgba(0,0,0,0)\">正文</span></p>");
        Assert.DoesNotContain("background-color", RichTextHtmlCodec.Encode(doc), StringComparison.Ordinal);
    }

    // ── W-10 ② 单元格 style 底色 ────────────────────────────────────

    [Fact]
    public void CellStyleBackgroundIsDecodedAndNormalized()
    {
        var doc = RichTextHtmlCodec.Decode(
            "<table><tr><td style=\"background-color:#F2F2F4\">A</td></tr></table>");
        var cell = Assert.Single(Assert.Single(doc.Blocks).Table!.Rows[0]);
        Assert.Equal("#F2F2F4", cell.BackgroundHex);
        Assert.Contains("data-bg=\"#F2F2F4\"", RichTextHtmlCodec.Encode(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void CellRgbBackgroundIsNormalized()
    {
        var doc = RichTextHtmlCodec.Decode(
            "<table><tr><td style=\"background-color: rgb(242, 242, 244)\">A</td></tr></table>");
        var cell = Assert.Single(Assert.Single(doc.Blocks).Table!.Rows[0]);
        Assert.Equal("#F2F2F4", cell.BackgroundHex);
    }

    [Fact]
    public void TbodyWrapperIsUnwrappedAndNotWrittenBack()
    {
        var doc = RichTextHtmlCodec.Decode(
            "<table><tbody><tr><td>A</td></tr></tbody></table>");
        var encoded = RichTextHtmlCodec.Encode(doc);
        Assert.DoesNotContain("tbody", encoded, StringComparison.Ordinal);
        Assert.Single(Assert.Single(doc.Blocks).Table!.Rows);
    }

    // ── W-10 ③ 内联图片转存判定 ─────────────────────────────────────

    [Fact]
    public void RelativeAttachmentPathIsAlreadyAllowed()
    {
        var result = InlineImageImport.Import("attachments/DDDD/4444.png", Guid.NewGuid(), null);
        Assert.Equal(InlineImageImport.Outcome.AlreadyRelative, result.Kind);
    }

    [Theory]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("file:///C:/Users/test/Desktop/a.png")]
    [InlineData("C:\\Users\\test\\Desktop\\a.png")]
    public void DataAndFilePathsAreRecognizedButFailWithoutStore(string src)
    {
        // 无 store 时无法落盘 → Failed，调用方据此拒写整篇
        var result = InlineImageImport.Import(src, Guid.NewGuid(), null);
        Assert.Equal(InlineImageImport.Outcome.Failed, result.Kind);
        Assert.DoesNotContain("不认识", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void BlobAndRemoteUrlsAreRejected()
    {
        var blob = InlineImageImport.Import("blob:https://example.com/uuid", Guid.NewGuid(), null);
        Assert.Equal(InlineImageImport.Outcome.Failed, blob.Kind);
        Assert.Contains("blob", blob.Error, StringComparison.OrdinalIgnoreCase);

        var http = InlineImageImport.Import("https://example.com/a.png", Guid.NewGuid(), null);
        Assert.Equal(InlineImageImport.Outcome.Failed, http.Kind);
    }

    [Fact]
    public void DataUriIsImportedToRelativeAttachmentPath()
    {
        var dir = TempDir();
        try
        {
            var store = new AttachmentStore(dir);
            var noteId = Guid.NewGuid();
            // 1×1 透明 PNG
            const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8AAAwAB/AF+gX8AAAAASUVORK5CYII=";
            var result = InlineImageImport.Import("data:image/png;base64," + png, noteId, store);

            Assert.Equal(InlineImageImport.Outcome.Imported, result.Kind);
            Assert.StartsWith("attachments/" + noteId.ToString("D").ToUpperInvariant() + "/", result.Path, StringComparison.Ordinal);
            Assert.EndsWith(".png", result.Path, StringComparison.Ordinal);
            Assert.True(File.Exists(store.AbsolutePath(result.Path)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LocalFileIsImportedToRelativeAttachmentPath()
    {
        var dir = TempDir();
        try
        {
            var source = Path.Combine(dir, "source.png");
            File.WriteAllBytes(source, [1, 2, 3, 4]);
            var store = new AttachmentStore(Path.Combine(dir, "data"));
            var noteId = Guid.NewGuid();
            var result = InlineImageImport.Import(source, noteId, store);

            Assert.Equal(InlineImageImport.Outcome.Imported, result.Kind);
            Assert.True(InlineImageImport.IsAllowed(result.Path));
            Assert.True(File.Exists(store.AbsolutePath(result.Path)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── AllRuns 覆盖表格单元格 ──────────────────────────────────────

    [Fact]
    public void AllRunsIncludesTableCellImages()
    {
        var doc = RichTextHtmlCodec.Decode(
            "<table><tr><td><img src=\"attachments/EEEE/5555.png\"></td></tr></table>");
        Assert.Single(doc.AllRuns().Where(run => run.Image != null).ToList());
    }

    // ── MarkdownConverter 不被本次改动破坏 ─────────────────────────

    [Fact]
    public void MarkdownConverterProducesControlledHtml()
    {
        var doc = MarkdownConverter.ToDocument("# 标题\n\n- 一\n- 二");
        var encoded = RichTextHtmlCodec.Encode(doc);
        Assert.StartsWith("<h1>", encoded, StringComparison.Ordinal);
        Assert.Contains("<ul>", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("data-tn-inline-src", encoded, StringComparison.Ordinal);
    }
}
