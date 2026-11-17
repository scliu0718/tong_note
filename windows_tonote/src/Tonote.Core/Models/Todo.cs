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

    /// <summary>深拷贝一份，供同步回写/冲突副本使用。</summary>
    public static Todo CopyOf(Todo source) => new()
    {
        CompletedAt = source.CompletedAt,
        CreatedAt = source.CreatedAt,
        Id = source.Id,
        Notes = source.Notes,
        Order = source.Order,
        PlannedAt = source.PlannedAt,
        Purged = source.Purged,
        Rev = source.Rev,
        StartedAt = source.StartedAt,
        Status = source.Status,
        Title = source.Title,
        UpdatedAt = source.UpdatedAt,
    };

    [JsonIgnore]
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

    /// <summary>
    /// 与 Mac 版一致：已完成 / 已终止的任务内容只读，但状态流转与删除仍然可用。
    /// 这是纯计算属性，不能进云端 JSON（System.Text.Json 会默认序列化只读属性）。
    /// </summary>
    [JsonIgnore]
    public bool IsTerminal => Status is TodoStatus.done or TodoStatus.terminated;
}
