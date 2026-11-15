using System.Globalization;
using System.Text;

namespace Tonote.Core;

/// <summary>
/// 表格 ↔ 受控 HTML。单元格复用正文那一套段落标签，保证加粗、颜色、换段与普通笔记同一条编解码路径。
/// <table data-widths="0.28,0.72" data-fullwidth="0"><tr><td data-align="center" data-bg="#F2F2F4">…</td></tr></table>
/// </summary>
public static class RichTextTableCodec
{
    public static string Encode(RichTextTable table)
    {
        var html = new StringBuilder("<table");
        var weights = table.EncodedWeights;
        if (weights != null)
            html.Append(" data-widths=\"").Append(RichTextHtmlCodec.EscapeAttribute(weights)).Append('"');
        // 满宽是默认值，不写属性，保证老笔记编码结果与之前完全一致
        if (!table.FullWidth)
            html.Append(" data-fullwidth=\"0\"");
        html.Append('>');
        foreach (var row in table.Rows)
        {
            html.Append("<tr>");
            foreach (var cell in row)
            {
                var inner = RichTextHtmlCodec.Encode(new RichTextDocument(cell.Blocks));
                // 左对齐、无背景色是默认值，不写属性
                var align = cell.Alignment == RichTextCellAlignment.left
                    ? ""
                    : $" data-align=\"{cell.Alignment}\"";
                var background = cell.BackgroundHex == null
                    ? ""
                    : $" data-bg=\"{RichTextHtmlCodec.EscapeAttribute(cell.BackgroundHex)}\"";
                html.Append("<td").Append(align).Append(background).Append('>')
                    .Append(inner).Append("</td>");
            }

            html.Append("</tr>");
        }

        return html.Append("</table>").ToString();
    }

    public static RichTextTable Decode(Dictionary<string, string> attributes, List<HtmlNode> children)
    {
        var rows = new List<List<RichTextTableCell>>();
        // 浏览器 innerHTML 会把 <tr> 包进 <tbody>，这里兼容 tbody/thead/tfoot 一层或多层包装
        foreach (var rowNode in RowNodes(children))
        {
            var cells = new List<RichTextTableCell>();
            foreach (var cellNode in rowNode.Children)
            {
                if (cellNode is not HtmlNode.ElementNode cellElement
                    || (cellElement.Tag != "td" && cellElement.Tag != "th"))
                    continue;
                var blocks = RichTextHtmlCodec.BlocksFrom(cellElement.Children)
                    .Where(block => block.Style != RichTextBlockStyle.table)
                    .ToList();
                cells.Add(new RichTextTableCell(blocks,
                    RichTextCellAlignmentText.FromHtmlAttributes(cellElement.Attributes),
                    BackgroundHex(cellElement.Attributes)));
            }

            if (cells.Count > 0)
                rows.Add(cells);
        }

        var columnCount = Math.Max(rows.Count == 0 ? 1 : rows.Max(row => row.Count), 1);
        attributes.TryGetValue("data-widths", out var rawWeights);
        var weights = RichTextTable.Weights(rawWeights, columnCount);
        var fullWidth = !attributes.TryGetValue("data-fullwidth", out var full) || full != "0";
        return new RichTextTable(rows, weights, fullWidth);
    }

    private static IEnumerable<HtmlNode.ElementNode> RowNodes(List<HtmlNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is not HtmlNode.ElementNode element)
                continue;
            if (element.Tag == "tr")
            {
                yield return element;
                continue;
            }
            if (element.Tag is "tbody" or "thead" or "tfoot" or "table")
            {
                foreach (var nested in RowNodes(element.Children))
                    yield return nested;
            }
        }
    }

    /// <summary>兼容外部粘贴进来的 style="background-color:#RRGGBB" 或 rgb(r,g,b)。</summary>
    private static string? BackgroundHex(Dictionary<string, string> attributes)
    {
        if (attributes.TryGetValue("data-bg", out var raw))
            return RichTextCellColor.Normalized(raw);

        var style = (attributes.TryGetValue("style", out var s) ? s : "").ToLowerInvariant();
        var marker = style.IndexOf("background-color:", StringComparison.Ordinal);
        if (marker < 0)
            return null;
        var value = style[(marker + "background-color:".Length)..];
        var end = value.IndexOfAny([';', '"', '\'']);
        if (end >= 0)
            value = value[..end];
        var trimmed = value.Trim();
        if (trimmed.StartsWith('#'))
            return RichTextCellColor.Normalized(trimmed);
        if (trimmed.StartsWith("rgb", StringComparison.Ordinal))
        {
            var numbers = System.Text.RegularExpressions.Regex.Split(trimmed, "[^0-9.]+")
                .Select(piece => double.TryParse(piece, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var number) ? number : double.NaN)
                .Where(number => !double.IsNaN(number))
                .Take(3).ToList();
            if (numbers.Count >= 3)
            {
                var parts = numbers.Select(number => (int)Math.Min(Math.Max(number, 0), 255)).ToList();
                return $"#{parts[0]:X2}{parts[1]:X2}{parts[2]:X2}";
            }
        }

        return null;
    }
}
