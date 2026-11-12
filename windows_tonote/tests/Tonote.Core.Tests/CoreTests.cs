using Tonote.Core;
using Xunit;

namespace Tonote.Core.Tests;

public class CoreTests
{
    [Fact]
    public void ReadsMacJsonAndFillsDefaults()
    {
        var dir = Temp();
        File.WriteAllText(Path.Combine(dir, "notes.json"), """
        [
          {
            "body" : "<p>A&amp;B</p>",
            "contentFormat" : "richText",
            "createdAt" : "2026-11-01T00:00:00Z",
            "id" : "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE",
            "pinned" : true,
            "rev" : 3,
            "tags" : [ "会议" ],
            "title" : "来自 Mac",
            "updatedAt" : "2026-11-02T01:02:03Z"
          },
          {
            "body" : "旧笔记",
            "createdAt" : "2026-01-01T00:00:00Z",
            "id" : "BBBBBBBB-BBBB-CCCC-DDDD-EEEEEEEEEEEE",
            "title" : "缺字段",
            "updatedAt" : "2026-01-02T00:00:00Z"
          }
        ]
        """);

        var store = new NoteStore(dir);
        var mac = store.Find(Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"));
        var legacy = store.Find(Guid.Parse("BBBBBBBB-BBBB-CCCC-DDDD-EEEEEEEEEEEE"));

        Assert.NotNull(mac);
        Assert.Equal("来自 Mac", mac!.Title);
        Assert.Equal(NoteContentFormat.richText, mac.ContentFormat);
        Assert.Equal("A&B", mac.VisibleBody);
        Assert.Equal(3, mac.Rev);
        Assert.Equal("工作日志", mac.Notebook);
        Assert.NotNull(legacy);
        Assert.Equal(NoteContentFormat.markdown, legacy!.ContentFormat);
        Assert.Equal("工作日志", legacy.Notebook);
        Assert.Empty(legacy.Tags);
        Assert.Contains(store.Notes, note => note.IsQuickSync);
    }

    [Fact]
    public void SavesIso8601WithoutFractionalSeconds()
    {
        var dir = Temp();
        var store = new NoteStore(dir);
        var note = store.AddNote();
        var json = File.ReadAllText(Path.Combine(dir, "notes.json"));

        Assert.Contains(note.Id.ToString("D").ToUpperInvariant(), json, StringComparison.Ordinal);
        Assert.Matches("""
            "createdAt": "\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z"
            """, json);
        Assert.DoesNotMatch(@"T\d{2}:\d{2}:\d{2}\.\d+Z", json);
    }

    [Fact]
    public void TrashKeepsTheNoteAndPermanentDeleteKeepsATombstone()
    {
        var store = new NoteStore(Temp());
        var note = store.AddNote();

        store.MoveToTrash(note.Id);
        Assert.Contains(store.TrashNotes, item => item.Id == note.Id);

        store.Restore(note.Id);
        Assert.Contains(store.ActiveNotes, item => item.Id == note.Id);

        store.PermanentlyDelete(note.Id);
        Assert.True(store.Find(note.Id)!.Purged);
        Assert.DoesNotContain(store.ActiveNotes, item => item.Id == note.Id);
        Assert.DoesNotContain(store.TrashNotes, item => item.Id == note.Id);
    }

    [Fact]
    public void QuickSyncCannotBeTrashed()
    {
        var store = new NoteStore(Temp());
        store.MoveToTrash(Note.QuickSyncNoteId);
        Assert.False(store.Find(Note.QuickSyncNoteId)!.IsTrashed);
    }

    [Fact]
    public void CorruptFileFallsBackToSamples()
    {
        var dir = Temp();
        File.WriteAllText(Path.Combine(dir, "notes.json"), "{");
        var store = new NoteStore(dir);
        Assert.Contains(store.Notes, note => note.Title == "产品经理周会纪要");
    }

    [Fact]
    public void NotebookNameRules()
    {
        var books = new NotebookStore(Temp());
        Assert.Equal("笔记本名称不能为空", books.Add("  "));
        Assert.Equal("「全部笔记」是内置项，不能用作笔记本名称", books.Add("全部笔记"));
        Assert.Equal("该笔记本已存在", books.Add("工作日志"));
        Assert.Null(books.Add("客户拜访"));
        Assert.Contains("客户拜访", books.List);
        Assert.NotNull(books.Add("这是一个超过十二个字的笔记本名"));
    }

    [Fact]
    public void DeletingNotebookMovesNotesButKeepsThem()
    {
        var dir = Temp();
        var notes = new NoteStore(dir);
        var books = new NotebookStore(dir);
        Assert.Null(books.Add("客户拜访"));
        var note = notes.AddNote("客户拜访");
        books.Remove("客户拜访");
        notes.MoveNotes("客户拜访", Notebooks.All);
        Assert.Equal(Notebooks.All, notes.Find(note.Id)!.Notebook);
        Assert.Contains("客户拜访", books.Removed);
    }

    [Fact]
    public void TodoMissingFieldsDefault()
    {
        var dir = Temp();
        File.WriteAllText(Path.Combine(dir, "todos.json"), """
        [{"id":"CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC","title":"写周报","createdAt":"2026-11-01T00:00:00Z","updatedAt":"2026-11-01T00:00:00Z"}]
        """);
        var store = new TodoStore(dir);
        var todo = Assert.Single(store.Todos);
        Assert.Equal("写周报", todo.Title);
        Assert.Equal(TodoStatus.todo, todo.Status);
        Assert.Equal("", todo.Notes);
        Assert.False(todo.Purged);
    }

    [Fact]
    public void VisibleTextStripsTagsAndDecodesEntities()
    {
        Assert.Equal("A&B", VisibleText.From("<p>A&amp;B</p>"));
        Assert.Equal("&amp;", VisibleText.From("&amp;"));
        Assert.Equal("你好", VisibleText.From("<h1>你好</h1>"));
        Assert.Equal("空笔记", new Note { Body = "  \n ", ContentFormat = NoteContentFormat.markdown }.Snippet);
    }

    [Fact]
    public void DevRootDoesNotUseTheMacFolderName()
    {
        Assert.EndsWith($"{Path.DirectorySeparatorChar}tonote-windows", AppPaths.RootFor(false, "/tmp/data"));
        Assert.EndsWith($"{Path.DirectorySeparatorChar}tonote", AppPaths.RootFor(true, "/tmp/data"));
    }

    [Fact]
    public void SecretFileSuppliesAppKeyWithoutLivingInSource()
    {
        var dir = Temp();
        var path = Path.Combine(dir, "baidupan.secret.json");
        File.WriteAllText(path, """{"appKey":"test-key","appSecret":"test-secret","appID":"1"}""");

        BaiduPanConfig.Load([path]);

        Assert.True(BaiduPanConfig.IsConfigured);
        Assert.Equal("test-key", BaiduPanConfig.AppKey);
        Assert.Equal("1", BaiduPanConfig.AppId);
        Assert.Equal("/apps/tong_log", BaiduPanConfig.RemoteRoot);
    }

    [Fact]
    public void AuthorizeUrlMatchesTheMacQueryAndCallbackParsesCode()
    {
        var path = Path.Combine(Temp(), "baidupan.secret.json");
        File.WriteAllText(path, """{"appKey":"app-key","appSecret":"app-secret","appID":"9"}""");
        BaiduPanConfig.Load([path]);

        var url = BaiduPanAuth.AuthorizeUrl("STATE-1", "page");

        Assert.StartsWith("https://openapi.baidu.com/oauth/2.0/authorize?", url, StringComparison.Ordinal);
        Assert.Contains("response_type=code", url, StringComparison.Ordinal);
        Assert.Contains("client_id=app-key", url, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=tongnote%3A%2F%2Foauth%2Fcallback", url, StringComparison.Ordinal);
        Assert.Contains("scope=basic%2Cnetdisk", url, StringComparison.Ordinal);
        Assert.Contains("display=page", url, StringComparison.Ordinal);
        Assert.Contains("state=STATE-1", url, StringComparison.Ordinal);

        Assert.True(BaiduPanAuth.TryParseCallback(
            "tongnote://oauth/callback?code=abc&state=STATE-1",
            out var callback));
        Assert.Equal("abc", callback.Code);
        Assert.Equal("STATE-1", callback.State);
        Assert.Null(callback.Error);

        Assert.True(BaiduPanAuth.TryParseCallback(
            "tongnote://oauth/callback?error=access_denied&state=STATE-1",
            out var denied));
        Assert.Equal("access_denied", denied.Error);
        Assert.False(BaiduPanAuth.TryParseCallback("https://example.com/", out _));
    }

    [Fact]
    public async Task ExchangeCodeStoresTokenInTheMacShape()
    {
        var path = Path.Combine(Temp(), "baidupan.secret.json");
        File.WriteAllText(path, """{"appKey":"app-key","appSecret":"app-secret","appID":"9"}""");
        BaiduPanConfig.Load([path]);

        var handler = new StubHandler("""
            {"access_token":"token-1","refresh_token":"refresh-1","expires_in":2592000,"scope":"basic netdisk"}
            """);
        using var http = new HttpClient(handler);
        var token = await BaiduPanClient.ExchangeCodeAsync("the-code", http);

        Assert.Equal("token-1", token.AccessToken);
        Assert.Equal("refresh-1", token.RefreshToken);
        Assert.Equal(2592000, token.ExpiresIn);
        Assert.Contains("grant_type=authorization_code", handler.RequestUrl, StringComparison.Ordinal);
        Assert.Contains("code=the-code", handler.RequestUrl, StringComparison.Ordinal);
        Assert.Contains("client_id=app-key", handler.RequestUrl, StringComparison.Ordinal);

        var dir = Temp();
        new BaiduPanTokenStore(dir).Save(token);
        var json = File.ReadAllText(Path.Combine(dir, "baidupan-token.json"));
        Assert.Contains("\"accessToken\": \"token-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"refreshToken\": \"refresh-1\"", json, StringComparison.Ordinal);
        Assert.Matches("""
            "createdAt": "\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z"
            """, json);

        var loaded = new BaiduPanTokenStore(dir).Load();
        Assert.Equal("token-1", loaded!.AccessToken);
        Assert.False(loaded.IsExpired);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StubHandler(string body) => _body = body;

        public string RequestUrl { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUrl = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_body),
            });
        }
    }

    private static string Temp() => Directory.CreateTempSubdirectory("tonote-win-").FullName;
}
