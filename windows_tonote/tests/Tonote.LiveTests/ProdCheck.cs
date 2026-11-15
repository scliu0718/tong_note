using System.Text;
using System.Text.Json.Serialization;
using Tonote.Core;

namespace Tonote.LiveTests;

/// <summary>
/// H 组：生产真实数据只读验收。
///
/// 与 A~G 不同，本组不设置 TONOTE_REMOTE_ROOT，直接读取 /apps/tong_log
/// （Mac / Android / Windows 三端共用的生产目录），用来回答「正式版有没有
/// 真的用上真实百度网盘的数据」。全程只调用列目录与下载接口，不启动同步
/// 引擎、不写任何云端文件；本地只读正式数据根的 json，另用一个临时目录
/// 检查新构建的空目录行为。
/// </summary>
public static class ProdCheck
{
    public static async Task<int> RunAsync()
    {
        var runner = new Runner();
        runner.Section("H 生产真实网盘数据只读验收");

        BaiduPanConfig.Load(new[] { Paths.Secret });
        var prod = BaiduPanConfig.DefaultRemoteRoot;
        var root = AppPaths.DefaultRoot();

        runner.Check("H01 正式进程远端根指向生产目录（不是测试沙箱）",
            BaiduPanConfig.RemoteRoot == prod
            && Environment.GetEnvironmentVariable("TONOTE_REMOTE_ROOT") == null,
            $"RemoteRoot={BaiduPanConfig.RemoteRoot} 数据根={root}");

        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var client = new BaiduPanClient(root, http);
        if (client.CurrentToken == null)
        {
            runner.Check("H02 正式数据根存在有效登录令牌", false, "未找到 baidupan-token.json");
            return runner.Report();
        }

        string access;
        try
        {
            access = (await client.ValidTokenAsync()).AccessToken;
            runner.Check("H02 正式数据根存在有效登录令牌", true,
                $"过期={client.CurrentToken.IsExpired}");
        }
        catch (Exception ex)
        {
            runner.Check("H02 正式数据根存在有效登录令牌", false, Runner.Redact(ex.Message));
            return runner.Report();
        }

        // ── 生产按篇笔记 ──────────────────────────────────────────────
        var noteFiles = await ListJson(client, prod + "/notes", access);
        runner.Check("H03 生产 notes/ 有真实笔记", noteFiles.Count > 0,
            $"{noteFiles.Count} 篇");

        var cloudNotes = new List<Note>();
        var badNotes = new List<string>();
        foreach (var name in noteFiles)
        {
            try
            {
                var bytes = await client.DownloadDataAsync(prod + "/notes/" + name, access);
                var note = TonoteJson.Deserialize<Note>(Encoding.UTF8.GetString(bytes));
                if (note == null) badNotes.Add(name + ":null");
                else cloudNotes.Add(note);
            }
            catch (Exception ex)
            {
                badNotes.Add(name + ":" + ex.GetType().Name);
            }
        }
        runner.Check("H04 Windows 端能解析全部生产笔记",
            cloudNotes.Count == noteFiles.Count && badNotes.Count == 0,
            $"成功 {cloudNotes.Count}/{noteFiles.Count}"
            + (badNotes.Count == 0 ? "" : " 失败=" + string.Join(",", badNotes.Take(3))));

        var localNotes = ReadLocal<List<Note>>(Path.Combine(root, "notes.json"));
        var cloudIds = cloudNotes.Select(note => note.Id).ToHashSet();
        var localIds = localNotes.Select(note => note.Id).ToHashSet();
        runner.Check("H05 正式数据根与生产网盘笔记 ID 一一对应",
            cloudIds.Count > 0 && cloudIds.SetEquals(localIds),
            $"云端 {cloudIds.Count} 本地 {localIds.Count} 仅云端 {cloudIds.Except(localIds).Count()} 仅本地 {localIds.Except(cloudIds).Count()}");

        var roundTripOk = cloudNotes.All(note =>
        {
            var again = TonoteJson.Deserialize<Note>(TonoteJson.Serialize(note));
            return again != null && again.Id == note.Id && again.Title == note.Title
                   && again.Body == note.Body && again.Rev == note.Rev
                   && again.UpdatedAt == note.UpdatedAt;
        });
        runner.Check("H06 真实笔记 encode→decode 幂等（跨端契约不丢字段）",
            cloudNotes.Count > 0 && roundTripOk, $"{cloudNotes.Count} 篇全部往返校验");

        // ── 生产待办 ──────────────────────────────────────────────────
        var todoFiles = await ListJson(client, prod + "/todos", access);
        var cloudTodos = new List<Todo>();
        foreach (var name in todoFiles)
        {
            try
            {
                var bytes = await client.DownloadDataAsync(prod + "/todos/" + name, access);
                var todo = TonoteJson.Deserialize<Todo>(Encoding.UTF8.GetString(bytes));
                if (todo != null) cloudTodos.Add(todo);
            }
            catch { /* 计入数量比对 */ }
        }
        var localTodos = ReadLocal<List<Todo>>(Path.Combine(root, "todos.json"));
        runner.Check("H07 生产待办全部可解析且本地一致",
            todoFiles.Count > 0 && cloudTodos.Count == todoFiles.Count
            && cloudTodos.Select(t => t.Id).ToHashSet()
                .SetEquals(localTodos.Select(t => t.Id).ToHashSet()),
            $"云端 {todoFiles.Count} 解析 {cloudTodos.Count} 本地 {localTodos.Count}");

        // ── Mac 端旧清单：Windows 是否已完整吸收 ──────────────────────
        try
        {
            var bytes = await client.DownloadDataAsync(prod + "/manifest.json", access);
            var manifest = TonoteJson.Deserialize<OldManifest>(Encoding.UTF8.GetString(bytes));
            var missingNotes = manifest?.Notes.Count(note => !localIds.Contains(note.Id)) ?? -1;
            var localTodoIds = localTodos.Select(t => t.Id).ToHashSet();
            var missingTodos = manifest?.Todos.Count(todo => !localTodoIds.Contains(todo.Id)) ?? -1;
            runner.Check("H08 Mac 旧清单里的真实笔记/待办已全部落到 Windows",
                manifest != null && missingNotes == 0 && missingTodos == 0,
                $"清单笔记 {manifest?.Notes.Count ?? 0} 缺失 {missingNotes} · 清单待办 {manifest?.Todos.Count ?? 0} 缺失 {missingTodos}");
        }
        catch (Exception ex)
        {
            runner.Check("H08 Mac 旧清单里的真实笔记/待办已全部落到 Windows", false,
                Runner.Redact(ex.Message));
        }

        // ── 新构建空目录行为：不再灌演示笔记 ──────────────────────────
        var temp = Path.Combine(Path.GetTempPath(), "tonote_prodcheck_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var fresh = new NoteStore(temp);
            var titles = fresh.Notes.Select(note => note.Title).ToList();
            runner.Check("H09 空数据目录不再灌入演示笔记",
                fresh.Notes.Count <= 1 && titles.All(title => title == "快同步"),
                $"空目录笔记数 {fresh.Notes.Count}：{string.Join("/", titles.Take(4))}");
        }
        catch (Exception ex)
        {
            runner.Check("H09 空数据目录不再灌入演示笔记", false, Runner.Redact(ex.Message));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 临时目录清理失败不影响结论 */ }
        }

        return runner.Report();
    }

    private static async Task<List<string>> ListJson(BaiduPanClient client, string dir, string token)
    {
        try
        {
            var entries = await client.ListFilesAsync(dir, token);
            return entries.Where(item => item.IsDir == 0
                    && item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Name).ToList();
        }
        catch (BaiduPanNotFoundException)
        {
            return [];
        }
    }

    private static T ReadLocal<T>(string path) where T : new()
    {
        try
        {
            return File.Exists(path)
                ? TonoteJson.Deserialize<T>(File.ReadAllText(path)) ?? new T()
                : new T();
        }
        catch
        {
            return new T();
        }
    }

    private sealed class OldManifest
    {
        [JsonPropertyName("notes")]
        public List<Note> Notes { get; set; } = [];

        [JsonPropertyName("todos")]
        public List<Todo> Todos { get; set; } = [];
    }
}
