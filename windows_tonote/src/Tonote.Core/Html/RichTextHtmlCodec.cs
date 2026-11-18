using System.Globalization;
using System.Text;

namespace Tonote.Core;

/// <summary>
/// 受控 HTML 编解码。与 Mac 版 RichTextHTMLCodec 逐条对齐，保证三端解析一致：
/// 只允许固定标签与属性；正文不出现 file:///Attachment.tiff；图片只存相对路径。
/// </summary>
public static class RichTextHtmlCodec
{
    // MARK: - 编码

    public static string Encode(RichTextDocument document)
    {
        var html = new StringBuilder();
        var blocks = document.Blocks;
        var index = 0;
        while (index < blocks.Count)
        {
            var block = blocks[index];
            if (block.Style is RichTextBlockStyle.bulletItem or RichTextBlockStyle.orderedItem)
            {
                // 连续的同类列表项合并成一个 <ul> / <ol>
                var items = new List<RichTextBlock>();
                var cursor = index;
                while (cursor < blocks.Count && blocks[cursor].Style == block.Style)
                {
                    items.Add(blocks[cursor]);
                    cursor++;
                }

                var tag = block.Style == RichTextBlockStyle.bulletItem ? "ul" : "ol";
                html.Append('<').Append(tag).Append('>');
                foreach (var item in items)
                {
                    var levelAttr = item.ListLevel > 0
                        ? $" data-level=\"{item.ListLevel}\""
                        : "";
                    html.Append("<li").Append(levelAttr).Append('>')
                        .Append(EncodeInline(item.Runs))
                        .Append("</li>");
                }

                html.Append("</").Append(tag).Append('>');
                index = cursor;
            }
            else
            {
                html.Append(EncodeBlock(block));
                index++;
            }

            // 块间换行分隔，最后一块不加尾随换行，保证「编码→解码→编码」稳定
            if (index < blocks.Count)
                html.Append('\n');
        }

        return html.ToString();
    }

    private static string EncodeBlock(RichTextBlock block)
    {
        // 标题本身即为加粗，段落内再套 <strong> 属于语义冗余
        var suppressBold = block.Style.IsHeading();
        var inner = EncodeInline(block.Runs, suppressBold);
        if (block.Style == RichTextBlockStyle.table)
            return block.Table != null ? RichTextTableCodec.Encode(block.Table) : "<p></p>";
        var tag = block.Style.Tag();
        return $"<{tag}>{inner}</{tag}>";
    }

    private static string EncodeInline(List<RichTextRun> runs, bool suppressBold = false)
    {
        var buffer = new StringBuilder();
        foreach (var run in runs)
            buffer.Append(EncodeRun(run, suppressBold));
        return buffer.ToString();
    }

    private static string EncodeRun(RichTextRun run, bool suppressBold)
    {
        if (run.Image != null)
        {
            var img = new StringBuilder("<img src=\"")
                .Append(EscapeAttribute(run.Image.Path))
                .Append('"');
            if (run.Image.Width != null)
                img.Append(" width=\"").Append(FormatNumber(run.Image.Width.Value)).Append('"');
            if (run.Image.Height != null)
                img.Append(" height=\"").Append(FormatNumber(run.Image.Height.Value)).Append('"');
            return img.Append('>').ToString();
        }

        var inner = EscapeText(run.Text);
        if (!string.IsNullOrEmpty(run.Link))
            inner = $"<a href=\"{EscapeAttribute(run.Link)}\">{inner}</a>";
        if (run.FontSize != null)
            inner = $"<span style=\"font-size:{FormatNumber(run.FontSize.Value)}px\">{inner}</span>";
        // PC-F-06 / PC-F-09（W-10）：默认正文色与引用样式色是视图层装饰，
        // 写进 HTML 会被 Mac / Android 当成显式颜色固化下来，必须省略。
        if (!IsDecorativeColor(run.ColorHex))
            inner = $"<span style=\"color:{EscapeAttribute(run.ColorHex!)}\">{inner}</span>";
        // 背景 alpha ≤ 0.01 视为无背景，不写 HTML
        if (!IsTransparentBackground(run.BackgroundHex))
            inner = $"<span style=\"background-color:{EscapeAttribute(run.BackgroundHex!)}\">{inner}</span>";
        if (run.IsStrike)
            inner = $"<s>{inner}</s>";
        if (run.IsUnderline)
            inner = $"<u>{inner}</u>";
        if (run.IsItalic)
            inner = $"<em>{inner}</em>";
        if (run.IsBold && !suppressBold)
            inner = $"<strong>{inner}</strong>";
        return inner;
    }

    private static string FormatNumber(double value)
        => Math.Abs(value - Math.Round(value)) < 0.0000001
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);

    private const string DefaultTextColor = "#1D1D1F";
    private const string QuoteTextColor = "#86868B";

    /// <summary>默认色 / 引用灰属于视图装饰，不得固化进 HTML（对齐 Mac RichTextBridge:751-761）。</summary>
    private static bool IsDecorativeColor(string? hex)
    {
        if (hex == null) return true;
        var normalized = NormalizeHex(hex);
        return normalized == null || normalized == DefaultTextColor || normalized == QuoteTextColor;
    }

    /// <summary>rgba(... , α≤0.01) / transparent / 空值都视为无背景。</summary>
    private static bool IsTransparentBackground(string? hex)
    {
        if (hex == null || hex.Length == 0) return true;
        var value = hex.Trim();
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return true;
        // rgba(0,0,0,0) / rgba(...,0.00)
        var comma = value.LastIndexOf(',');
        if (value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            var alphaPart = value[(comma + 1)..].Trim().TrimEnd(')').Trim();
            if (double.TryParse(alphaPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
                && alpha <= 0.01)
                return true;
        }
        return false;
    }

    /// <summary>把 #rgb / #rrggbb / rgb(r,g,b) 归一化为 #RRGGBB；无法识别返回 null。</summary>
    private static string? NormalizeHex(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('#'))
        {
            var digits = trimmed[1..];
            if (digits.Length == 6 && digits.All(Uri.IsHexDigit))
                return "#" + digits.ToUpperInvariant();
            if (digits.Length == 3 && digits.All(Uri.IsHexDigit))
                return "#" + string.Concat(digits.Select(c => char.ToUpperInvariant(c))).ToUpperInvariant();
            return null;
        }
        if (trimmed.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var parts = System.Text.RegularExpressions.Regex.Split(trimmed, "[^0-9.]+")
                .Where(piece => piece.Length > 0)
                .Take(3)
                .Select(piece => double.TryParse(piece, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var n) ? (int)Math.Clamp(n, 0, 255) : -1)
                .ToList();
            if (parts.Count == 3 && parts.All(part => part >= 0))
                return $"#{parts[0]:X2}{parts[1]:X2}{parts[2]:X2}";
        }
        return null;
    }

    // 用 \u0026 拼出 & 前缀，避免源码文件在写入链路里被当成 HTML 实体解码。
    private const string Amp = "\u0026amp;";
    private const string Lt = "\u0026lt;";
    private const string Gt = "\u0026gt;";
    private const string Quot = "\u0026quot;";
    private const string Apos = "\u0026#39;";

    public static string EscapeText(string text)
        => text.Replace("&", Amp, StringComparison.Ordinal)
            .Replace("<", Lt, StringComparison.Ordinal)
            .Replace(">", Gt, StringComparison.Ordinal);

    public static string EscapeAttribute(string text)
        => EscapeText(text)
            .Replace("\"", Quot, StringComparison.Ordinal)
            .Replace("'", Apos, StringComparison.Ordinal);

    // MARK: - 解码

    public static RichTextDocument Decode(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return RichTextDocument.Empty();
        var nodes = HtmlTreeBuilder.Parse(html);
        var blocks = BlocksFrom(nodes);
        return blocks.Count == 0 ? RichTextDocument.Empty() : new RichTextDocument(blocks);
    }

    /// <summary>把一组已解析节点还原成段落。表格单元格复用这条路径。</summary>
    public static List<RichTextBlock> BlocksFrom(List<HtmlNode> nodes)
    {
        var blocks = new List<RichTextBlock>();
        foreach (var node in nodes)
            blocks.AddRange(BlocksFrom(node));
        return blocks;
    }

    private static List<RichTextBlock> BlocksFrom(HtmlNode node)
    {
        switch (node)
        {
            case HtmlNode.TextNode textNode:
            {
                if (string.IsNullOrWhiteSpace(textNode.Text))
                    return [];
                return [new RichTextBlock(RichTextBlockStyle.paragraph,
                    [new RichTextRun { Text = textNode.Text }])];
            }
            case HtmlNode.ElementNode element:
            {
                switch (element.Tag)
                {
                    case "table":
                        return [new RichTextBlock(RichTextBlockStyle.table, [], 0,
                            RichTextTableCodec.Decode(element.Attributes, element.Children))];
                    case "p":
                        return ParagraphBlocks(element.Children);
                    case "h1": case "h2": case "h3":
                    case "h4": case "h5": case "h6":
                        return [new RichTextBlock(RichTextBlockStyleText.FromTag(element.Tag),
                            InlineRuns(element.Children, new InlineContext()))];
                    case "blockquote":
                        return [new RichTextBlock(RichTextBlockStyle.quote,
                            InlineRuns(element.Children, new InlineContext()))];
                    case "pre":
                        return [new RichTextBlock(RichTextBlockStyle.code,
                            [new RichTextRun { Text = HtmlTreeBuilder.RawText(element.Children) }])];
                    case "ul":
                        return ListBlocks(element.Children, RichTextBlockStyle.bulletItem);
                    case "ol":
                        return ListBlocks(element.Children, RichTextBlockStyle.orderedItem);
                    case "br":
                        return [];
                    case "img":
                        // W-07：块级位置的裸 <img>（Android 会产出 <p>文字</p><img …>）不能丢，
                        // 单独成段显示，复用 InlineRuns 的 img 属性解析，与 Mac 端一致。
                        return [new RichTextBlock(RichTextBlockStyle.paragraph,
                            InlineRuns([node], new InlineContext()))];
                    default:
                    {
                        var result = new List<RichTextBlock>();
                        foreach (var child in element.Children)
                            result.AddRange(BlocksFrom(child));
                        if (result.Count == 0)
                        {
                            var trimmed = HtmlTreeBuilder.RawText(element.Children).Trim();
                            if (trimmed.Length > 0)
                                result = [new RichTextBlock(RichTextBlockStyle.paragraph,
                                    [new RichTextRun { Text = trimmed }])];
                        }

                        return result;
                    }
                }
            }
            default:
                return [];
        }
    }

    private static List<RichTextBlock> ListBlocks(List<HtmlNode> children, RichTextBlockStyle style)
    {
        var result = new List<RichTextBlock>();
        foreach (var child in children)
        {
            if (child is HtmlNode.ElementNode item && item.Tag == "li")
            {
                var level = 0;
                if (item.Attributes.TryGetValue("data-level", out var raw))
                    int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out level);
                result.Add(new RichTextBlock(style,
                    InlineRuns(item.Children, new InlineContext()),
                    Math.Max(0, Math.Min(level, 7))));
            }
            else
            {
                result.AddRange(BlocksFrom(child));
            }
        }

        return result;
    }

    /// <summary>段落内的 <br> 会拆成多个段落。</summary>
    private static List<RichTextBlock> ParagraphBlocks(List<HtmlNode> nodes)
    {
        var segments = new List<List<HtmlNode>> { new List<HtmlNode>() };
        foreach (var node in nodes)
        {
            if (node is HtmlNode.ElementNode element && element.Tag == "br")
                segments.Add([]);
            else
                segments[^1].Add(node);
        }

        return segments.Select(segment => new RichTextBlock(RichTextBlockStyle.paragraph,
            InlineRuns(segment, new InlineContext()))).ToList();
    }

    private sealed class InlineContext
    {
        public bool IsBold;
        public bool IsItalic;
        public bool IsUnderline;
        public bool IsStrike;
        public double? FontSize;
        public string? ColorHex;
        public string? BackgroundHex;
        public string? Link;

        public InlineContext Clone() => (InlineContext)MemberwiseClone();
    }

    private static List<RichTextRun> InlineRuns(List<HtmlNode> nodes, InlineContext context)
    {
        var runs = new List<RichTextRun>();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case HtmlNode.TextNode textNode:
                    if (textNode.Text.Length == 0)
                        break;
                    runs.Add(new RichTextRun
                    {
                        Text = textNode.Text,
                        IsBold = context.IsBold,
                        IsItalic = context.IsItalic,
                        IsUnderline = context.IsUnderline,
                        IsStrike = context.IsStrike,
                        FontSize = context.FontSize,
                        ColorHex = context.ColorHex,
                        BackgroundHex = context.BackgroundHex,
                        Link = context.Link,
                    });
                    break;
                case HtmlNode.ElementNode element:
                    switch (element.Tag)
                    {
                        case "strong": case "b":
                        {
                            var ctx = context.Clone();
                            ctx.IsBold = true;
                            runs.AddRange(InlineRuns(element.Children, ctx));
                            break;
                        }
                        case "em": case "i":
                        {
                            var ctx = context.Clone();
                            ctx.IsItalic = true;
                            runs.AddRange(InlineRuns(element.Children, ctx));
                            break;
                        }
                        case "u": case "ins":
                        {
                            var ctx = context.Clone();
                            ctx.IsUnderline = true;
                            runs.AddRange(InlineRuns(element.Children, ctx));
                            break;
                        }
                        case "s": case "strike": case "del":
                        {
                            var ctx = context.Clone();
                            ctx.IsStrike = true;
                            runs.AddRange(InlineRuns(element.Children, ctx));
                            break;
                        }
                        case "span":
                        {
                            var ctx = context.Clone();
                            if (element.Attributes.TryGetValue("style", out var style))
                                ApplyStyle(style, ctx);
                            runs.AddRange(InlineRuns(element.Children, ctx));
                            break;
                        }
                        case "a":
                        {
                            var ctx = context.Clone();
                            if (element.Attributes.TryGetValue("href", out var href) && href.Length > 0)
                                ctx.Link = href;
                            runs.AddRange(InlineRuns(element.Children, ctx));
                            break;
                        }
                        case "img":
                        {
                            element.Attributes.TryGetValue("src", out var src);
                            var image = new RichTextImage(src ?? "");
                            if (element.Attributes.TryGetValue("width", out var width)
                                && double.TryParse(width, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out var widthValue))
                                image.Width = widthValue;
                            if (element.Attributes.TryGetValue("height", out var height)
                                && double.TryParse(height, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out var heightValue))
                                image.Height = heightValue;
                            runs.Add(new RichTextRun { Image = image });
                            break;
                        }
                        case "br":
                            runs.Add(new RichTextRun { Text = "\n" });
                            break;
                        default:
                            runs.AddRange(InlineRuns(element.Children, context));
                            break;
                    }

                    break;
            }
        }

        return runs;
    }

    private static void ApplyStyle(string? style, InlineContext context)
    {
        if (string.IsNullOrEmpty(style))
            return;
        foreach (var piece in style.Split(';'))
        {
            var separator = piece.IndexOf(':');
            if (separator < 0)
                continue;
            var key = piece[..separator].Trim().ToLowerInvariant();
            var value = piece[(separator + 1)..].Trim();
            switch (key)
            {
                case "font-size":
                {
                    var numeric = value.Replace("px", "", StringComparison.OrdinalIgnoreCase);
                    if (double.TryParse(numeric, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var size) && size > 0)
                        context.FontSize = size;
                    break;
                }
                case "color":
                    if (value.Length > 0)
                        context.ColorHex = value;
                    break;
                case "background-color": case "background":
                    // transparent / none 视为清除背景色
                    if (value.Length == 0
                        || value.Equals("transparent", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                        context.BackgroundHex = null;
                    else
                        context.BackgroundHex = value;
                    break;
            }
        }
    }
}
