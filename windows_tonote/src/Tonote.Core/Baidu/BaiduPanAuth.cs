namespace Tonote.Core;

public readonly record struct OAuthCallback(string? Code, string? State, string? Error);

/// <summary>
/// 授权地址和 tongnote:// 回调解析。换令牌在 <see cref="BaiduPanClient"/>。
/// </summary>
public static class BaiduPanAuth
{
    public static string AuthorizeUrl(string state, string display)
    {
        var query = string.Join("&", new[]
        {
            Pair("response_type", "code"),
            Pair("client_id", BaiduPanConfig.AppKey),
            Pair("redirect_uri", BaiduPanConfig.RedirectUri),
            Pair("scope", BaiduPanConfig.Scope),
            Pair("display", display),
            Pair("state", state),
        });
        return $"{BaiduPanConfig.AuthorizeUrl}?{query}";
    }

    public static bool TryParseCallback(string raw, out OAuthCallback callback)
    {
        callback = default;
        if (!Uri.TryCreate(raw.Trim().Trim('"'), UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, BaiduPanConfig.CallbackScheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var query = ParseQuery(uri.Query);
        callback = new OAuthCallback(
            query.GetValueOrDefault("code"),
            query.GetValueOrDefault("state"),
            query.GetValueOrDefault("error"));
        return true;
    }

    private static string Pair(string name, string value)
        => $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = query.TrimStart('?');
        if (text.Length == 0)
            return result;
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
            result[name] = value;
        }
        return result;
    }
}
