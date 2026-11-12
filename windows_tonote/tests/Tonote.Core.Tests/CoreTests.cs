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

    private static string Temp() => Directory.CreateTempSubdirectory("tonote-win-").FullName;
}
