namespace Tonote.Core;

/// <summary>段落级样式。取值与 Mac 版 RichTextBlockStyle 一致。</summary>
public enum RichTextBlockStyle
{
    paragraph,
    heading1,
    heading2,
    heading3,
    heading4,
    heading5,
    heading6,
    quote,
    code,
    bulletItem,
    orderedItem,
    table,
}

public static class RichTextBlockStyleText
{
    public static string Tag(this RichTextBlockStyle style) => style switch
    {
        RichTextBlockStyle.paragraph => "p",
        RichTextBlockStyle.heading1 => "h1",
        RichTextBlockStyle.heading2 => "h2",
        RichTextBlockStyle.heading3 => "h3",
        RichTextBlockStyle.heading4 => "h4",
        RichTextBlockStyle.heading5 => "h5",
        RichTextBlockStyle.heading6 => "h6",
        RichTextBlockStyle.quote => "blockquote",
        RichTextBlockStyle.code => "pre",
        _ => "p",
    };

    public static RichTextBlockStyle FromTag(string tag) => tag switch
    {
        "h1" => RichTextBlockStyle.heading1,
        "h2" => RichTextBlockStyle.heading2,
        "h3" => RichTextBlockStyle.heading3,
        "h4" => RichTextBlockStyle.heading4,
        "h5" => RichTextBlockStyle.heading5,
        "h6" => RichTextBlockStyle.heading6,
        "blockquote" => RichTextBlockStyle.quote,
        "pre" => RichTextBlockStyle.code,
        _ => RichTextBlockStyle.paragraph,
    };

    public static bool IsHeading(this RichTextBlockStyle style)
        => style is RichTextBlockStyle.heading1 or RichTextBlockStyle.heading2
            or RichTextBlockStyle.heading3 or RichTextBlockStyle.heading4
            or RichTextBlockStyle.heading5 or RichTextBlockStyle.heading6;
}

/// <summary>内嵌图片。只存相对路径，跨平台同步友好。</summary>
public sealed class RichTextImage
{
    /// <summary>形如 attachments/<note-uuid>/<image-uuid>.png</summary>
    public string Path { get; set; } = "";
    public double? Width { get; set; }
    public double? Height { get; set; }

    public RichTextImage() { }

    public RichTextImage(string path, double? width = null, double? height = null)
    {
        Path = path;
        Width = width;
        Height = height;
    }
}

/// <summary>段内一段连续格式的文本。</summary>
public sealed class RichTextRun
{
    public string Text { get; set; } = "";
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public bool IsStrike { get; set; }
    public double? FontSize { get; set; }
    public string? ColorHex { get; set; }
    public string? BackgroundHex { get; set; }
    public string? Link { get; set; }
    public RichTextImage? Image { get; set; }
}

/// <summary>一个段落 = 一行。样式为 table 时，格子内容在 Table 里，Runs 留空。</summary>
public sealed class RichTextBlock
{
    public RichTextBlockStyle Style { get; set; } = RichTextBlockStyle.paragraph;
    public List<RichTextRun> Runs { get; set; } = [];
    /// <summary>列表缩进层级 0~7，只影响展示，随 data-level 持久化。</summary>
    public int ListLevel { get; set; }
    public RichTextTable? Table { get; set; }

    public RichTextBlock() { }

    public RichTextBlock(RichTextBlockStyle style, List<RichTextRun>? runs = null,
        int listLevel = 0, RichTextTable? table = null)
    {
        Style = style;
        Runs = runs ?? [];
        ListLevel = listLevel;
        Table = table;
    }

    public string PlainText => Style == RichTextBlockStyle.table && Table != null
        ? Table.PlainText
        : string.Concat(Runs.Select(run => run.Image != null ? "" : run.Text));
}

public sealed class RichTextDocument
{
    public List<RichTextBlock> Blocks { get; set; } = [];

    public RichTextDocument() { }

    public RichTextDocument(List<RichTextBlock> blocks) => Blocks = blocks;

    public string PlainText => string.Join("\n", Blocks.Select(block => block.PlainText));

    public static RichTextDocument Empty()
        => new([new RichTextBlock(RichTextBlockStyle.paragraph)]);
}

/// <summary>单元格内文字的水平对齐。左对齐是默认值，不写进 HTML。</summary>
public enum RichTextCellAlignment
{
    left,
    center,
    right,
    justify,
}

public static class RichTextCellAlignmentText
{
    public static RichTextCellAlignment FromHtmlAttributes(Dictionary<string, string> attrs)
    {
        if (attrs.TryGetValue("data-align", out var raw))
        {
            raw = raw.Trim().ToLowerInvariant();
            if (Enum.TryParse<RichTextCellAlignment>(raw, out var parsed))
                return parsed;
        }

        var style = (attrs.TryGetValue("style", out var s) ? s : "")
            .ToLowerInvariant().Replace(" ", "").Replace("\t", "");
        if (style.Contains("text-align:justify", StringComparison.Ordinal))
            return RichTextCellAlignment.justify;
        if (style.Contains("text-align:center", StringComparison.Ordinal))
            return RichTextCellAlignment.center;
        if (style.Contains("text-align:right", StringComparison.Ordinal))
            return RichTextCellAlignment.right;
        return RichTextCellAlignment.left;
    }
}

/// <summary>颜色统一成 #RRGGBB，无效值视为「无颜色」。</summary>
public static class RichTextCellColor
{
    public static string? Normalized(string? raw)
    {
        if (raw == null)
            return null;
        var text = raw.Trim().ToUpperInvariant();
        if (text.StartsWith('#'))
            text = text[1..];
        if (text.Length != 6 || !text.All(Uri.IsHexDigit))
            return null;
        return "#" + text;
    }
}

/// <summary>表格的一个单元格。里面是若干普通段落，不再嵌套表格。</summary>
public sealed class RichTextTableCell
{
    public List<RichTextBlock> Blocks { get; set; }
    public RichTextCellAlignment Alignment { get; set; } = RichTextCellAlignment.left;
    public string? BackgroundHex { get; set; }

    public RichTextTableCell()
    {
        Blocks = [new RichTextBlock(RichTextBlockStyle.paragraph)];
    }

    public RichTextTableCell(List<RichTextBlock>? blocks,
        RichTextCellAlignment alignment = RichTextCellAlignment.left,
        string? backgroundHex = null, bool fillIfEmpty = true)
    {
        var kept = (blocks ?? []).Where(b => b.Style != RichTextBlockStyle.table).ToList();
        Blocks = kept.Count == 0 && fillIfEmpty
            ? [new RichTextBlock(RichTextBlockStyle.paragraph)]
            : kept;
        Alignment = alignment;
        BackgroundHex = RichTextCellColor.Normalized(backgroundHex);
    }

    public string PlainText => string.Join("\n", Blocks.Select(block =>
        string.Concat(block.Runs.Select(run => run.Image != null ? "" : run.Text))));
}

/// <summary>一张矩形表格。列宽权重和为 1；null 表示各列等宽。</summary>
public sealed class RichTextTable
{
    public const int MaxRows = 100;
    public const int MaxColumns = 32;
    public const double FixedColumnWidth = 100;

    public List<List<RichTextTableCell>> Rows { get; set; } = [];
    public List<double>? ColumnWeights { get; set; }
    /// <summary>true = 占满编辑区；false = 每列固定 100pt。</summary>
    public bool FullWidth { get; set; } = true;

    public RichTextTable() { }

    public RichTextTable(List<List<RichTextTableCell>> rows,
        List<double>? columnWeights = null, bool fullWidth = true)
    {
        var columnCount = Math.Max(rows.Count == 0 ? 1 : rows.Max(row => row.Count), 1);
        var source = rows.Count == 0
            ? [Enumerable.Range(0, columnCount).Select(_ => new RichTextTableCell()).ToList()]
            : rows;

        Rows = source.Select(row =>
        {
            var copy = row.Select(cell => new RichTextTableCell(cell.Blocks,
                cell.Alignment, cell.BackgroundHex)).ToList();
            if (copy.Count < columnCount)
            {
                for (var i = copy.Count; i < columnCount; i++)
                    copy.Add(new RichTextTableCell());
            }
            else if (copy.Count > columnCount)
            {
                copy = copy.Take(columnCount).ToList();
            }

            return copy;
        }).ToList();

        ColumnWeights = columnWeights != null
                        && columnWeights.Count == columnCount
                        && columnWeights.All(value => value > 0)
            ? columnWeights
            : null;
        FullWidth = fullWidth;
    }

    public int RowCount => Rows.Count;
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows[0].Count;

    public static RichTextTable Make(int rows, int columns)
    {
        var rowCount = Math.Min(Math.Max(rows, 1), MaxRows);
        var columnCount = Math.Min(Math.Max(columns, 1), MaxColumns);
        var row = Enumerable.Range(0, columnCount).Select(_ => new RichTextTableCell()).ToList();
        return new RichTextTable(
            Enumerable.Range(0, rowCount).Select(_ =>
                row.Select(cell => new RichTextTableCell(cell.Blocks)).ToList()).ToList(),
            null, fullWidth: false);
    }

    public string PlainText => string.Join("\n",
        Rows.Select(row => string.Join("\t", row.Select(cell => cell.PlainText))));

    public string? EncodedWeights
    {
        get
        {
            if (ColumnWeights == null || ColumnWeights.Count != ColumnCount)
                return null;
            return string.Join(",", ColumnWeights.Select(FormatWeight));
        }
    }

    public static List<double>? Weights(string? raw, int columnCount)
    {
        if (columnCount <= 0 || string.IsNullOrWhiteSpace(raw))
            return null;
        var parts = raw.Split(',')
            .Select(piece => double.TryParse(piece.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
            .ToList();
        if (parts.Count != columnCount || parts.Any(value => double.IsNaN(value) || value <= 0))
            return null;
        return parts;
    }

    public bool InsertRow(int index)
    {
        if (RowCount >= MaxRows || ColumnCount <= 0)
            return false;
        var insertion = Math.Min(Math.Max(index, 0), RowCount);
        var reference = insertion < Rows.Count ? Rows[insertion] : Rows[^1];
        var fresh = Enumerable.Range(0, ColumnCount).Select(column =>
            column < reference.Count
                ? new RichTextTableCell(null, reference[column].Alignment, reference[column].BackgroundHex)
                : new RichTextTableCell()).ToList();
        Rows.Insert(insertion, fresh);
        return true;
    }

    public bool RemoveRow(int index)
    {
        if (RowCount <= 1 || index < 0 || index >= RowCount)
            return false;
        Rows.RemoveAt(index);
        return true;
    }

    public bool InsertColumn(int index)
    {
        if (ColumnCount >= MaxColumns || RowCount <= 0)
            return false;
        var insertion = Math.Min(Math.Max(index, 0), ColumnCount);
        var referenceColumn = insertion > 0 ? insertion - 1 : Math.Min(insertion, ColumnCount - 1);
        foreach (var row in Rows)
        {
            if (referenceColumn >= row.Count)
            {
                row.Insert(Math.Min(insertion, row.Count), new RichTextTableCell());
                continue;
            }

            var source = row[referenceColumn];
            row.Insert(Math.Min(insertion, row.Count),
                new RichTextTableCell(null, source.Alignment, source.BackgroundHex));
        }

        ColumnWeights = null;
        return true;
    }

    public bool RemoveColumn(int index)
    {
        if (ColumnCount <= 1 || index < 0 || index >= ColumnCount)
            return false;
        foreach (var row in Rows)
            row.RemoveAt(index);
        ColumnWeights = null;
        return true;
    }

    private static string FormatWeight(double value)
    {
        var text = value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        while (text.Contains('.') && (text.EndsWith('0') || text.EndsWith('.')))
            text = text[..^1];
        return text.Length == 0 ? "0" : text;
    }
}
