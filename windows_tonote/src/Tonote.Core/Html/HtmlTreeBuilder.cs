using System.Text;
using System.Text.RegularExpressions;

namespace Tonote.Core;

/// <summary>极简 HTML 树的节点。只服务受控标签集，不是通用 HTML5 DOM。</summary>
public abstract class HtmlNode
{
    public sealed class TextNode : HtmlNode
    {
        public string Text { get; }
        public TextNode(string text) => Text = text;
    }

    public sealed class ElementNode : HtmlNode
    {
        public string Tag { get; }
        public Dictionary<string, string> Attributes { get; }
        public List<HtmlNode> Children { get; }

        public ElementNode(string tag, Dictionary<string, string> attributes, List<HtmlNode> children)
        {
            Tag = tag;
            Attributes = attributes;
            Children = children;
        }
    }
}

/// <summary>
/// 与 Mac 版 HTMLTreeBuilder 一致的栈式解析器：注释整段跳过、结束标签弹到最近的同名帧、
/// 未闭合标签末尾依次弹栈、style 重复出现用分号合并、其余属性重复时以首个为准。
/// </summary>
public static class HtmlTreeBuilder
{
    private static readonly HashSet<string> VoidTags =
    [
        "br", "img", "hr", "meta", "link", "input", "area", "base", "col", "source"
    ];

    private static readonly Regex AttributePattern = new(
        "([A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s\"'>]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class Frame
    {
        public string Tag = "";
        public Dictionary<string, string> Attributes = [];
        public List<HtmlNode> Children = [];
    }

    public static List<HtmlNode> Parse(string html)
    {
        var roots = new List<HtmlNode>();
        var stack = new List<Frame>();

        void Append(HtmlNode node)
        {
            if (stack.Count == 0)
                roots.Add(node);
            else
                stack[^1].Children.Add(node);
        }

        var text = new StringBuilder();
        void FlushText()
        {
            if (text.Length == 0)
                return;
            Append(new HtmlNode.TextNode(DecodeEntities(text.ToString())));
            text.Clear();
        }

        var index = 0;
        while (index < html.Length)
        {
            if (html[index] != '<')
            {
                text.Append(html[index]);
                index++;
                continue;
            }

            // 注释
            if (index + 3 < html.Length && html[index + 1] == '!' && html[index + 2] == '-' && html[index + 3] == '-')
            {
                FlushText();
                var cursor = index + 4;
                while (cursor + 2 < html.Length)
                {
                    if (html[cursor] == '-' && html[cursor + 1] == '-' && html[cursor + 2] == '>')
                    {
                        cursor += 3;
                        break;
                    }

                    cursor++;
                }

                index = cursor;
                continue;
            }

            // 结束标签
            if (index + 1 < html.Length && html[index + 1] == '/')
            {
                var cursor = index + 2;
                var tagBuilder = new StringBuilder();
                while (cursor < html.Length && html[cursor] != '>')
                {
                    tagBuilder.Append(html[cursor]);
                    cursor++;
                }

                if (cursor >= html.Length)
                {
                    text.Append(html[index..]);
                    break;
                }

                FlushText();
                var name = tagBuilder.ToString().Trim().ToLowerInvariant();
                var position = stack.FindLastIndex(frame => frame.Tag == name);
                if (position >= 0)
                {
                    while (stack.Count > position)
                    {
                        var frame = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        Append(new HtmlNode.ElementNode(frame.Tag, frame.Attributes, frame.Children));
                    }
                }

                index = cursor + 1;
                continue;
            }

            // 开始标签
            var tagCursor = index + 1;
            var startTag = new StringBuilder();
            while (tagCursor < html.Length
                   && !char.IsWhiteSpace(html[tagCursor])
                   && html[tagCursor] != '>'
                   && html[tagCursor] != '/')
            {
                startTag.Append(html[tagCursor]);
                tagCursor++;
            }

            var attributeBuilder = new StringBuilder();
            while (tagCursor < html.Length && html[tagCursor] != '>')
            {
                attributeBuilder.Append(html[tagCursor]);
                tagCursor++;
            }

            if (tagCursor >= html.Length)
            {
                text.Append(html[index..]);
                break;
            }

            FlushText();
            var attributeText = attributeBuilder.ToString();
            var selfClosing = attributeText.Trim().EndsWith('/');
            var tagName = startTag.ToString().ToLowerInvariant();
            var parsed = ParseAttributes(attributeText);
            if (selfClosing || VoidTags.Contains(tagName))
                Append(new HtmlNode.ElementNode(tagName, parsed, []));
            else
                stack.Add(new Frame { Tag = tagName, Attributes = parsed });

            index = tagCursor + 1;
        }

        FlushText();
        while (stack.Count > 0)
        {
            var frame = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            Append(new HtmlNode.ElementNode(frame.Tag, frame.Attributes, frame.Children));
        }

        return roots;
    }

    private static Dictionary<string, string> ParseAttributes(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in AttributePattern.Matches(raw))
        {
            if (!match.Success || match.Groups.Count < 3)
                continue;
            var key = match.Groups[1].Value.Trim().ToLowerInvariant();
            var value = match.Groups[2].Value.Trim();
            if (value.Length >= 2
                && ((value.StartsWith('"') && value.EndsWith('"'))
                    || (value.StartsWith('\'') && value.EndsWith('\''))))
                value = value[1..^1];
            if (key.Length == 0)
                continue;
            var decoded = DecodeEntities(value);
            if (key == "style")
            {
                // style 允许多次出现，合并而不是覆盖，避免字号与颜色互相丢失
                if (result.TryGetValue(key, out var existing) && existing.Length > 0)
                    result[key] = existing + ";" + decoded;
                else
                    result[key] = decoded;
            }
            else if (!result.ContainsKey(key))
            {
                result[key] = decoded;
            }
        }

        return result;
    }

    /// <summary>固定 7 项实体，与 Mac / Android 一致，不做通用 &#NNN; 解析。</summary>
    public static string DecodeEntities(string text)
        => VisibleText.DecodeEntities(text);

    public static string RawText(List<HtmlNode> nodes)
    {
        var buffer = new StringBuilder();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case HtmlNode.TextNode textNode:
                    buffer.Append(textNode.Text);
                    break;
                case HtmlNode.ElementNode element:
                    buffer.Append(RawText(element.Children));
                    break;
            }
        }

        return buffer.ToString();
    }
}
