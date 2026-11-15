using System.Text;
using System.Text.RegularExpressions;

namespace Tonote.Core;

/// <summary>
/// 旧 Markdown 正文的兼容转换器，与 Mac 版逐条对齐。
/// 旧笔记里 ![](图片地址) 这类占位符不是真实附件，按纯文本处理，
/// 绝不把占位符误当成真实图片写进附件目录。
/// </summary>
public static class MarkdownConverter
{
    public static RichTextDocument ToDocument(string markdown)
    {
        var blocks = new List<RichTextBlock>();
        var lines = markdown.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var listBuffer = new List<RichTextBlock>();
        var paragraphBuffer = new List<RichTextRun>();

        void FlushList()
        {
            if (listBuffer.Count == 0)
                return;
            blocks.AddRange(listBuffer);
            listBuffer.Clear();
        }

        void FlushParagraph()
        {
            if (paragraphBuffer.Count == 0)
                return;
            blocks.Add(new RichTextBlock(RichTextBlockStyle.paragraph, paragraphBuffer));
            paragraphBuffer = [];
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            // 代码块围栏：整行丢弃，不进入 code 块状态
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            // 标题，仅到三级
            if (TryHeading(trimmed, out var heading))
            {
                FlushParagraph();
                FlushList();
                blocks.Add(heading!);
                continue;
            }

            // 无序列表
            if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                FlushParagraph();
                listBuffer.Add(new RichTextBlock(RichTextBlockStyle.bulletItem,
                    InlineRuns(trimmed[2..])));
                continue;
            }

            // 有序列表
            var ordered = OrderedListContent(trimmed);
            if (ordered != null)
            {
                FlushParagraph();
                listBuffer.Add(new RichTextBlock(RichTextBlockStyle.orderedItem,
                    InlineRuns(ordered)));
                continue;
            }

            // 引用
            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                blocks.Add(new RichTextBlock(RichTextBlockStyle.quote,
                    InlineRuns(trimmed[2..])));
                continue;
            }

            FlushList();
            paragraphBuffer.AddRange(InlineRuns(line));
        }

        FlushParagraph();
        FlushList();
        return blocks.Count == 0 ? RichTextDocument.Empty() : new RichTextDocument(blocks);
    }

    public static string PlainTextFromDocument(RichTextDocument document)
        => string.Join("\n", document.Blocks.Select(block =>
        {
            var text = block.PlainText;
            return block.Style == RichTextBlockStyle.bulletItem ? "• " + text : text;
        }));

    /// <summary>正文 → 纯文本，自动识别 Markdown / HTML 两种格式。</summary>
    public static string PlainText(string body, NoteContentFormat format)
        => format == NoteContentFormat.richText
            ? RichTextHtmlCodec.Decode(body).PlainText
            : PlainTextFromMarkdown(body);

    public static string PlainTextFromMarkdown(string markdown)
    {
        var lines = new List<string>();
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            foreach (var marker in new[] { "### ", "## ", "# ", "> ", "- ", "* ", "+ " })
            {
                if (!line.StartsWith(marker, StringComparison.Ordinal))
                    continue;
                line = line[marker.Length..];
                break;
            }

            line = Regex.Replace(line, @"^\d+\.\s*", "");
            line = line.Replace("**", "", StringComparison.Ordinal)
                .Replace("*", "", StringComparison.Ordinal)
                .Replace("`", "", StringComparison.Ordinal)
                .Replace("~~", "", StringComparison.Ordinal);
            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    private static bool TryHeading(string line, out RichTextBlock? block)
    {
        var prefixes = new (string Prefix, RichTextBlockStyle Style)[]
        {
            ("### ", RichTextBlockStyle.heading3),
            ("## ", RichTextBlockStyle.heading2),
            ("# ", RichTextBlockStyle.heading1),
        };
        foreach (var (prefix, style) in prefixes)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            block = new RichTextBlock(style, InlineRuns(line[prefix.Length..]));
            return true;
        }

        block = null;
        return false;
    }

    private static string? OrderedListContent(string line)
    {
        var match = Regex.Match(line, @"^\d+\.\s+");
        return match.Success ? line[match.Length..] : null;
    }

    /// <summary>行内标记解析：**加粗**、*斜体*、`代码`、~~删除~~、[文字](链接)、![图片](路径)</summary>
    private static List<RichTextRun> InlineRuns(string text)
    {
        var runs = new List<RichTextRun>();
        var buffer = new StringBuilder();
        var index = 0;

        void Flush()
        {
            if (buffer.Length == 0)
                return;
            runs.Add(new RichTextRun { Text = buffer.ToString() });
            buffer.Clear();
        }

        int? FindClose(string delimiter, int start)
        {
            var position = text.IndexOf(delimiter, start, StringComparison.Ordinal);
            return position >= 0 ? position : null;
        }

        while (index < text.Length)
        {
            // **加粗**
            if (text.AsSpan(index).StartsWith("**")
                && FindClose("**", index + 2) is { } boldClose)
            {
                Flush();
                runs.Add(new RichTextRun
                {
                    Text = text[(index + 2)..boldClose],
                    IsBold = true,
                });
                index = boldClose + 2;
                continue;
            }

            // ~~删除~~
            if (text.AsSpan(index).StartsWith("~~")
                && FindClose("~~", index + 2) is { } strikeClose)
            {
                Flush();
                runs.Add(new RichTextRun
                {
                    Text = text[(index + 2)..strikeClose],
                    IsStrike = true,
                });
                index = strikeClose + 2;
                continue;
            }

            // `代码`
            if (text[index] == '`' && FindClose("`", index + 1) is { } codeClose)
            {
                Flush();
                runs.Add(new RichTextRun
                {
                    Text = text[(index + 1)..codeClose],
                    FontSize = 13,
                });
                index = codeClose + 1;
                continue;
            }

            // ![图片](路径)
            if (text.AsSpan(index).StartsWith("![")
                && FindClose("]", index + 2) is { } altClose
                && altClose + 1 < text.Length && text[altClose + 1] == '('
                && FindClose(")", altClose + 2) is { } imageClose)
            {
                Flush();
                var path = text[(altClose + 2)..imageClose];
                if (path.StartsWith("attachments/", StringComparison.Ordinal))
                {
                    runs.Add(new RichTextRun { Image = new RichTextImage(path) });
                }
                else
                {
                    var alt = text[(index + 2)..altClose];
                    runs.Add(new RichTextRun { Text = $"![{alt}]({path})" });
                }

                index = imageClose + 1;
                continue;
            }

            // [文字](链接)
            if (text[index] == '['
                && FindClose("]", index + 1) is { } labelClose
                && labelClose + 1 < text.Length && text[labelClose + 1] == '('
                && FindClose(")", labelClose + 2) is { } linkClose)
            {
                Flush();
                runs.Add(new RichTextRun
                {
                    Text = text[(index + 1)..labelClose],
                    Link = text[(labelClose + 2)..linkClose],
                });
                index = linkClose + 1;
                continue;
            }

            // *斜体*
            if (text[index] == '*' && FindClose("*", index + 1) is { } italicClose
                && italicClose > index + 1)
            {
                Flush();
                runs.Add(new RichTextRun
                {
                    Text = text[(index + 1)..italicClose],
                    IsItalic = true,
                });
                index = italicClose + 1;
                continue;
            }

            buffer.Append(text[index]);
            index++;
        }

        Flush();
        return runs.Count == 0 ? [new RichTextRun()] : runs;
    }
}
