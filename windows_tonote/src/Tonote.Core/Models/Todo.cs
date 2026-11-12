using System.Text.Json.Serialization;

namespace Tonote.Core;

public enum TodoStatus
{
    todo,
    doing,
    done,
    terminated,
}

public static class TodoStatusText
{
    public static string Label(this TodoStatus status) => status switch
    {
        TodoStatus.todo => "待完成",
        TodoStatus.doing => "进行中",
        TodoStatus.done => "已完成",
        TodoStatus.terminated => "已终止",
        _ => status.ToString(),
    };
}

public sealed class Todo
{
    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("plannedAt")]
    public DateTime? PlannedAt { get; set; }

    [JsonPropertyName("purged")]
    public bool Purged { get; set; }

    [JsonPropertyName("rev")]
    public int Rev { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("status")]
    public TodoStatus Status { get; set; } = TodoStatus.todo;

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsOverdue
    {
        get
        {
            if (Purged || Status is TodoStatus.done or TodoStatus.terminated || PlannedAt == null)
                return false;
            var start = DateTime.Now.Date;
            return PlannedAt.Value.ToLocalTime() < start;
        }
    }
}
