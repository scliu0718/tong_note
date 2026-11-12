using System.Text.Json.Serialization;

namespace Tonote.Core;

public sealed class BaiduPanException : Exception
{
    public BaiduPanException(string message) : base(message) { }
}

/// <summary>
/// 用授权码换访问令牌。请求形式与 Mac 版一致，走 GET。
/// </summary>
public static class BaiduPanClient
{
    public static async Task<BaiduPanToken> ExchangeCodeAsync(
        string code,
        HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        if (!BaiduPanConfig.IsConfigured)
            throw new BaiduPanException("尚未配置 AppKey / AppSecret");

        var query = string.Join("&", new[]
        {
            Pair("grant_type", "authorization_code"),
            Pair("code", code),
            Pair("client_id", BaiduPanConfig.AppKey),
            Pair("client_secret", BaiduPanConfig.AppSecret),
            Pair("redirect_uri", BaiduPanConfig.RedirectUri),
        });
        var url = $"{BaiduPanConfig.TokenUrl}?{query}";

        var own = http == null;
        http ??= new HttpClient();
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = TonoteJson.Deserialize<TokenResponse>(body)
                ?? throw new BaiduPanException("授权响应为空");
            if (!string.IsNullOrWhiteSpace(parsed.Error) || string.IsNullOrWhiteSpace(parsed.AccessToken))
            {
                throw new BaiduPanException(
                    string.IsNullOrWhiteSpace(parsed.ErrorDescription) ? (parsed.Error ?? "授权失败") : parsed.ErrorDescription);
            }

            return new BaiduPanToken
            {
                AccessToken = parsed.AccessToken,
                RefreshToken = parsed.RefreshToken ?? "",
                ExpiresIn = parsed.ExpiresIn,
                Scope = parsed.Scope,
                CreatedAt = DateTime.UtcNow,
            };
        }
        finally
        {
            if (own)
                http.Dispose();
        }
    }

    private static string Pair(string name, string value)
        => $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}
