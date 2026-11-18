using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tonote.Core;

/// <summary>
/// 与 Mac 版 JSONEncoder 对齐：ISO 8601 到秒、缺字段可省略、中文不转义。
/// Swift 的 prettyPrinted 会在冒号前多一个空格，读写都不依赖这个空格。
/// </summary>
public static class TonoteJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// W-38：读文件失败时先把原件备份为 *.corrupt-时间戳，再回退空集合。
    /// 绝不静默覆盖损坏文件，便于事后恢复。
    /// </summary>
    public static T? TryDeserializeFile<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            return Deserialize<T>(File.ReadAllText(path));
        }
        catch
        {
            BackupCorrupt(path);
            return default;
        }
    }

    public static void BackupCorrupt(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var backup = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(path, backup, overwrite: false);
        }
        catch
        {
            // 备份失败不能让应用起不来；原文件仍在，可人工拷出
        }
    }

    public static void WriteAtomic(string path, string json)
        => WriteAtomicBytes(path, Encoding.UTF8.GetBytes(json));

    public static void WriteAtomicBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch
            {
                if (attempt >= 2) throw; // W-38：失败重试 3 次后交调用方报错
                Thread.Sleep(60);
            }
        }
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        // W-40：对齐 Mac 的 .sortedKeys。属性按名字排序后，同一份数据重复写盘 diff 为空。
        // Note/Todo 的属性本就是字母序，所以云端 JSON 字节不变；只消除本机文件的噪音。
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object) return;
            var order = 0;
            foreach (var property in info.Properties.OrderBy(item => item.Name, StringComparer.Ordinal))
                property.Order = order++;
        });
        options.TypeInfoResolver = resolver;
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UppercaseGuidConverter());
        options.Converters.Add(new Iso8601DateTimeConverter());
        options.Converters.Add(new Iso8601NullableDateTimeConverter());
        return options;
    }

    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static string FormatUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString(Format, CultureInfo.InvariantCulture);
    }

    private static DateTime ParseUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new JsonException("时间为空");
        if (!DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new JsonException($"无法解析时间：{text}");
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private sealed class Iso8601DateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => ParseUtc(reader.GetString());

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            => writer.WriteStringValue(FormatUtc(value));
    }

    private sealed class Iso8601NullableDateTimeConverter : JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;
            return ParseUtc(reader.GetString());
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(FormatUtc(value.Value));
        }
    }

    private sealed class UppercaseGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetGuid();

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
    }
}
