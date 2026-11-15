using System.Text;

namespace Tonote.Core;

/// <summary>
/// 在文本中识别「本地图片路径」。与 Mac 版规则一致，另加 Windows 盘符 / UNC 起点支持：
/// 从扩展名确定终点，回溯到引号/括号边界，再按斜杠边界从长到短尝试真实存在的文件。
/// 允许路径本身含空格。
/// </summary>
public static class ImagePathScanner
{
    public static HashSet<string> ImageExtensions => AttachmentStore.AllowedExtensions;

    public sealed class Segment
    {
        public string Text { get; set; } = "";
        public string? ImageUrl { get; set; }
        public bool IsImage => ImageUrl != null;

        public static Segment TextSegment(string value) => new() { Text = value };
        public static Segment ImageSegment(string value, string url) => new() { Text = value, ImageUrl = url };
    }

    public static List<Segment> Segments(string? text)
    {
        var result = new List<Segment>();
        if (string.IsNullOrEmpty(text))
            return result;

        var pending = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            var match = MatchImagePath(text, index);
            if (match != null)
            {
                if (pending.Length > 0)
                {
                    result.Add(Segment.TextSegment(pending.ToString()));
                    pending.Clear();
                }

                result.Add(Segment.ImageSegment(match.Path, match.AbsolutePath));
                index = match.End;
                continue;
            }

            pending.Append(text[index]);
            index++;
        }

        if (pending.Length > 0)
            result.Add(Segment.TextSegment(pending.ToString()));
        return result;
    }

    public static bool ContainsImagePath(string? text)
        => Segments(text).Any(segment => segment.IsImage);

    private sealed record PathMatch(string Path, string AbsolutePath, int End);

    private static PathMatch? MatchImagePath(string text, int index)
    {
        // 起点必须是 /（Unix）或盘符 X:\ / UNC \\（Windows）
        if (!IsPathStart(text, index))
            return null;

        var cursor = index;
        var extEnd = -1;
        while (cursor < text.Length)
        {
            if (text[cursor] == '.')
            {
                var j = cursor + 1;
                while (j < text.Length && char.IsLetterOrDigit(text[j]))
                    j++;
                var ext = text[(cursor + 1)..j].ToLowerInvariant();
                if (ImageExtensions.Contains(ext))
                {
                    // 扩展名后不能紧跟字母数字（避免把 a.png2 当成图片）
                    if (j < text.Length && char.IsLetterOrDigit(text[j]))
                        break;
                    extEnd = j;
                }

                break;
            }

            var ch = text[cursor];
            if (char.IsWhiteSpace(ch) || ch is '"' or '\'' or '<' or '>')
                break;
            cursor++;
        }

        if (extEnd < 0)
            return null;

        // 回溯到边界，允许路径本身含空格
        var start = index;
        while (start > 0 && text[start - 1] is not ('"' or '\'' or '(' or '[' or '<'))
            start--;

        // 片段可能包含前面的普通文字，按斜杠边界从长到短尝试真实存在的文件。
        // Windows 盘符路径（C:\...）要把盘符一起带上，不能从反斜杠处截断。
        var hasDrivePrefix = index + 2 < text.Length
                             && char.IsLetter(text[index])
                             && text[index + 1] == ':'
                             && text[index + 2] is '\\' or '/';
        for (var candidateStart = start; candidateStart < extEnd; candidateStart++)
        {
            var boundary = text[candidateStart] is '/' or '\\';
            if (!boundary && !(hasDrivePrefix && candidateStart == index))
                continue;
            var candidate = text[candidateStart..extEnd];
            var validated = ValidatedImagePath(candidate);
            if (validated != null)
                return new PathMatch(candidate, validated, extEnd);
        }

        return null;
    }

    private static bool IsPathStart(string text, int index)
    {
        if (index >= text.Length)
            return false;
        if (text[index] == '/')
            return true;
        if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] == '\\')
            return true;
        // 盘符：X:\ 或 X:/
        return index + 2 < text.Length
               && char.IsLetter(text[index])
               && text[index + 1] == ':'
               && text[index + 2] is '\\' or '/';
    }

    private static string? ValidatedImagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var full = Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(full) && !Directory.Exists(full))
                return full;
        }
        catch
        {
            // 非法路径跳过
        }

        return null;
    }
}
