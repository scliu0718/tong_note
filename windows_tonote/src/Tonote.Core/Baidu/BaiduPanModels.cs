using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tonote.Core;

/// <summary>网盘用户信息（xpan uinfo）。</summary>
public sealed class BaiduPanUserInfo
{
    [JsonPropertyName("baidu_name")]
    public string? BaiduName { get; set; }

    [JsonPropertyName("netdisk_name")]
    public string? NetdiskName { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("vip_type")]
    public int? VipType { get; set; }

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(NetdiskName)
        ? NetdiskName!
        : !string.IsNullOrWhiteSpace(BaiduName) ? BaiduName! : "百度网盘用户";
}

/// <summary>网盘配额（xpan quota），单位字节。</summary>
public sealed class BaiduPanQuota
{
    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("used")]
    public long Used { get; set; }

    [JsonIgnore]
    public double UsedRatio => Total <= 0 ? 0 : Math.Clamp((double)Used / Total, 0, 1);

    [JsonIgnore]
    public string UsedText => Format(Used);

    [JsonIgnore]
    public string TotalText => Format(Total);

    private static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

/// <summary>网盘目录列表中的一项（xpan file list）。</summary>
public sealed class BaiduPanListEntry
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("server_filename")]
    public string? ServerFilename { get; set; }

    [JsonPropertyName("isdir")]
    public int IsDir { get; set; }

    /// <summary>百度有时返回数字、有时返回字符串，用转换器兼容。</summary>
    [JsonPropertyName("server_mtime")]
    [JsonConverter(typeof(FlexibleInt64Converter))]
    public long? ServerMtime { get; set; }

    /// <summary>文件 ID。errno 20020 时要用它走 filemetas 取下载直链。</summary>
    [JsonPropertyName("fs_id")]
    [JsonConverter(typeof(FlexibleInt64Converter))]
    public long? FsId { get; set; }

    [JsonPropertyName("size")]
    [JsonConverter(typeof(FlexibleInt64Converter))]
    public long? Size { get; set; }

    [JsonIgnore]
    public string Name => ServerFilename
        ?? System.IO.Path.GetFileName(Path ?? "")
        ?? "";
}

/// <summary>兼容 number / string / null 的整数解析。</summary>
public sealed class FlexibleInt64Converter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var integer)) return integer;
                if (reader.TryGetDouble(out var real)) return (long)real;
                return null;
            case JsonTokenType.String:
                var text = reader.GetString();
                return long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}
