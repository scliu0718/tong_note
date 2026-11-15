using System.Text;
using Tonote.Core;

namespace Tonote.LiveTests;

/// <summary>C 组共用的本地仓库 + 远端读写辅助。</summary>
public sealed class SyncHarness
{
    public required LiveContext Ctx { get; init; }
    public required NoteStore Notes { get; init; }
    public required TodoStore Todos { get; init; }
    public required NotebookStore Notebooks { get; init; }
    public required SyncEngine Engine { get; init; }

    public string At => Ctx.AccessToken;

    public static SyncHarness Create(LiveContext ctx)
    {
        foreach (var name in new[]
        {
            "notes.json", "todos.json", "notebooks.json", "note-sync.json", "todo-sync.json",
            "last-sync.json", "device.json", "avatar.json", "sync-cursors.json",
        })
        {
            var path = Path.Combine(ctx.LocalRoot, name);
            if (File.Exists(path)) File.Delete(path);
        }
        var attachments = Path.Combine(ctx.LocalRoot, "attachments");
        if (Directory.Exists(attachments)) Directory.Delete(attachments, recursive: true);

        var notes = new NoteStore(ctx.LocalRoot);
        notes.SetNotes([]);                 // 去掉 NoteSamples 的示例数据
        var todos = new TodoStore(ctx.LocalRoot);
        todos.SetTodos([]);
        var notebooks = new NotebookStore(ctx.LocalRoot);
        var engine = new SyncEngine(notes, todos, notebooks, ctx.LocalRoot, ctx.Client);
        return new SyncHarness
        {
            Ctx = ctx, Notes = notes, Todos = todos, Notebooks = notebooks, Engine = engine,
        };
    }

    public static string FileName(Guid id) => id.ToString("D").ToUpperInvariant() + ".json";

    public string NoteRemote(Guid id) => Ctx.Remote("notes/" + FileName(id));
    public string TodoRemote(Guid id) => Ctx.Remote("todos/" + FileName(id));

    public async Task<List<BaiduPanListEntry>> ListNotes()
    {
        try { return await Ctx.Client.ListFilesAsync(Ctx.Remote("notes"), At); }
        catch (BaiduPanNotFoundException) { return []; }
    }

    public async Task<List<BaiduPanListEntry>> ListTodos()
    {
        try { return await Ctx.Client.ListFilesAsync(Ctx.Remote("todos"), At); }
        catch (BaiduPanNotFoundException) { return []; }
    }

    public async Task<string> ReadRaw(string remotePath)
        => Encoding.UTF8.GetString(await Ctx.Client.DownloadDataAsync(remotePath, At));

    public async Task<Note?> ReadRemoteNote(Guid id)
    {
        try { return TonoteJson.Deserialize<Note>(await ReadRaw(NoteRemote(id))); }
        catch (BaiduPanNotFoundException) { return null; }
    }

    public async Task<Todo?> ReadRemoteTodo(Guid id)
    {
        try { return TonoteJson.Deserialize<Todo>(await ReadRaw(TodoRemote(id))); }
        catch (BaiduPanNotFoundException) { return null; }
    }

    public Task WriteRemoteNote(Note note)
        => Ctx.Client.UploadDataAsync(
            Encoding.UTF8.GetBytes(TonoteJson.Serialize(note)), NoteRemote(note.Id), At);

    public Task WriteRemoteTodo(Todo todo)
        => Ctx.Client.UploadDataAsync(
            Encoding.UTF8.GetBytes(TonoteJson.Serialize(todo)), TodoRemote(todo.Id), At);

    public Task WriteRemoteRaw(string remotePath, string text)
        => Ctx.Client.UploadDataAsync(Encoding.UTF8.GetBytes(text), remotePath, At);

    /// <summary>同步引擎内部会吞掉异常只改状态，必须显式断言 Success。</summary>
    public async Task<bool> SyncRound(Runner r, string label)
    {
        await Engine.QuickSyncAsync();
        var ok = Engine.Status.Kind == SyncStatusKind.Success;
        r.Check(label, ok, (Engine.LastMessage ?? "") + (ok ? "" : " · 状态=" + Engine.Status.Kind));
        return ok;
    }
}
