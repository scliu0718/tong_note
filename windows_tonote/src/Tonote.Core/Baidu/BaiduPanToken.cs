using System.Text.Json.Serialization;

namespace Tonote.Core;

/// <summary>
/// 与 Mac 版 baidupan-token.json 字段一致。提前 5 分钟视为过期。
/// </summary>
public sealed class BaiduPanToken
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsExpired =>
        DateTime.UtcNow >= CreatedAt.ToUniversalTime().AddSeconds(ExpiresIn).AddMinutes(-5);
}
