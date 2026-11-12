namespace Tonote.Core;

/// <summary>
/// 去掉受控 HTML 标签后的可见文字，供摘要、搜索和字数使用。
/// 不建文档树。实体表与 Mac 版一致，只处理固定的 7 个。
/// </summary>
public static class VisibleText
{
    public static string From(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return "";
        if (!html.Contains('<', StringComparison.Ordinal))
            return html;

        var buffer = new System.Text.StringBuilder(Math.Min(html.Length, 8000));
        for (var i = 0; i < html.Length; i++)
        {
            if (html[i] == '<')
            {
                var end = html.IndexOf('>', i + 1);
                if (end >= 0)
                {
                    i = end;
                    continue;
                }
            }

            buffer.Append(html[i]);
        }

        return DecodeEntities(buffer.ToString());
    }

    public static string DecodeEntities(string text)
    {
        return text
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
    }
}
