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

    public bool IsTrashed => DeletedAt != null;

    public bool IsQuickSync => Id == QuickSyncNoteId;

    public string VisibleBody => ContentFormat == NoteContentFormat.richText
        ? VisibleText.From(Body)
        : Body;

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

    public int WordCount => VisibleBody.Count(ch => !char.IsWhiteSpace(ch));
}
