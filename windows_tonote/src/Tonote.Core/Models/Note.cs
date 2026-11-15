using System.Text.Json.Serialization;

namespace Tonote.Core;

public sealed class Note
{
    public static readonly Guid QuickSyncNoteId = Guid.Parse("E1C2D3A4-B5F6-47C8-9D0E-1F2A3B4C5D6E");

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("contentFormat")]
    public NoteContentFormat ContentFormat { get; set; } = NoteContentFormat.markdown;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("deletedAt")]
    public DateTime? DeletedAt { get; set; }

    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("notebook")]
    public string Notebook { get; set; } = "工作日志";

    [JsonPropertyName("pinned")]
    public bool Pinned { get; set; }

    [JsonPropertyName("purged")]
    public bool Purged { get; set; }

    [JsonPropertyName("rev")]
    public int Rev { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsTrashed => DeletedAt != null;

    [JsonIgnore]
    public bool IsQuickSync => Id == QuickSyncNoteId;

    /// <summary>深拷贝一份，供同步回写/冲突副本使用，避免直接改到界面持有的引用。</summary>
    public static Note CopyOf(Note source) => new()
    {
        Body = source.Body,
        ContentFormat = source.ContentFormat,
        CreatedAt = source.CreatedAt,
        DeletedAt = source.DeletedAt,
        Id = source.Id,
        Notebook = source.Notebook,
        Pinned = source.Pinned,
        Purged = source.Purged,
        Rev = source.Rev,
        Tags = [.. source.Tags],
        Title = source.Title,
        UpdatedAt = source.UpdatedAt,
    };

    [JsonIgnore]
    public string VisibleBody => ContentFormat == NoteContentFormat.richText
        ? VisibleText.From(Body)
        : Body;

    [JsonIgnore]
    public string Snippet
    {
        get
        {
            var source = VisibleBody.Replace('\n', ' ').Trim();
            if (source.Length == 0)
                return "空笔记";
            return source.Length <= 60 ? source : source[..60];
        }
    }

    [JsonIgnore]
    public string TimeText
    {
        get
        {
            var local = UpdatedAt.Kind == DateTimeKind.Utc ? UpdatedAt.ToLocalTime() : UpdatedAt;
            var now = DateTime.Now;
            if (local.Date == now.Date)
                return "今天 " + local.ToString("HH:mm");
            if (local.Date == now.Date.AddDays(-1))
                return "昨天 " + local.ToString("HH:mm");
            return local.ToString("M月d日");
        }
    }

    [JsonIgnore]
    public int WordCount => VisibleBody.Count(ch => !char.IsWhiteSpace(ch));
}
