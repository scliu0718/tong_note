using System.Text;
using Tonote.Core;

namespace Tonote.LiveTests;

/// <summary>C 组：真实网盘上的按篇同步引擎（笔记 / 待办 / 笔记本 / 附件 / 冲突 / 迁移）。</summary>
public static class GroupC
{
    public static async Task RunAsync(LiveContext ctx, Runner r)
    {
        r.Section("C 同步引擎（真实网盘按篇同步）");
        var h = SyncHarness.Create(ctx);
        var at = ctx.AccessToken;

        var now = DateTime.UtcNow;
        var n1 = new Note
        {
            Id = Guid.NewGuid(), Title = "C01 Markdown 笔记",
            Body = "# 标题\n\n正文 **加粗** 与 `代码`", ContentFormat = NoteContentFormat.markdown,
            Tags = ["真实", "同步"], Notebook = "工作日志", CreatedAt = now, UpdatedAt = now,
        };
        var n2 = new Note
        {
            Id = Guid.NewGuid(), Title = "C02 富文本笔记",
            Body = "<p>富文本 <b>加粗</b></p><table data-fullwidth=\"true\"><tbody><tr>"
                   + "<td data-align=\"center\" data-bg=\"#EEEEEE\">单元格</td></tr></tbody></table>",
            ContentFormat = NoteContentFormat.richText, CreatedAt = now, UpdatedAt = now,
        };
        var n3 = new Note
        {
            Id = Guid.NewGuid(), Title = "C03 置顶带标签", Body = "置顶笔记正文",
            Pinned = true, Tags = ["标签A", "标签B"], Notebook = "测试本",
            CreatedAt = now, UpdatedAt = now,
        };
        h.Notes.SetNotes([n1, n2, n3]);
        r.Check("C01 本地基线为 3 篇笔记（已清空示例数据）",
            h.Notes.Notes.Count == 3 && h.Todos.Todos.Count == 0,
            $"笔记={h.Notes.Notes.Count} 待办={h.Todos.Todos.Count}");

        await h.SyncRound(r, "C02 首轮完整同步成功（设备登记 + 笔记 + 笔记本 + 待办）");

        await r.Case("C03 三篇笔记按 UUID 落到 notes/", async () =>
        {
            var entries = await h.ListNotes();
            var ids = entries.Where(item => item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Name[..^5]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (ids.Contains(n1.Id.ToString("D")) && ids.Contains(n2.Id.ToString("D"))
                    && ids.Contains(n3.Id.ToString("D")),
                $"云端 {entries.Count} 项：{string.Join(",", entries.Select(item => item.Name).Take(6))}");
        });

        await r.Case("C04 云端 JSON 字段集与 Mac 端真实契约逐字一致", async () =>
        {
            var raw = await h.ReadRaw(h.NoteRemote(n2.Id));
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var actual = doc.RootElement.EnumerateObject()
                .Select(prop => prop.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            // 生产目录里 Mac 端按篇笔记的真实 keys（deletedAt 为 nil 时按 encodeIfPresent 省略）
            string[] expected =
            [
                "body", "contentFormat", "createdAt", "id", "notebook", "pinned",
                "purged", "rev", "tags", "title", "updatedAt",
            ];
            string[] forbidden = ["visibleBody", "snippet", "timeText", "wordCount", "isTrashed", "isQuickSync"];
            var leaked = forbidden.Where(name => raw.Contains("\"" + name + "\"",
                StringComparison.OrdinalIgnoreCase)).ToArray();
            var parsed = TonoteJson.Deserialize<Note>(raw);
            return (actual.SequenceEqual(expected) && leaked.Length == 0
                    && parsed?.Title == n2.Title && parsed.ContentFormat == NoteContentFormat.richText,
                $"实际=[{string.Join(",", actual)}] 泄漏=[{string.Join(",", leaked)}] 标题={parsed?.Title}");
        });

        await r.Case("C05 无改动再同步应幂等（0 篇变更）", async () =>
        {
            var before = await h.ListNotes();
            await h.Engine.QuickSyncAsync();
            var after = await h.ListNotes();
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && (h.Engine.LastMessage ?? "").Contains("笔记 0 篇")
                    && before.Count == after.Count,
                $"状态={h.Engine.Status.Kind} 消息={h.Engine.LastMessage} 文件数 {before.Count}→{after.Count}");
        });

        // ── 云端 → 本机 ─────────────────────────────────────────────
        var remoteNewId = Guid.NewGuid();
        var remoteNew = new Note
        {
            Id = remoteNewId, Title = "C06 云端新增笔记", Body = "<p>来自 Mac 端</p>",
            ContentFormat = NoteContentFormat.richText, Rev = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Tags = ["云端"],
        };
        await h.WriteRemoteNote(remoteNew);
        await h.SyncRound(r, "C06 云端新增笔记触发的同步轮成功");
        r.Check("C07 云端新增笔记被拉到本机",
            h.Notes.Find(remoteNewId)?.Title == "C06 云端新增笔记"
            && h.Notes.Find(remoteNewId)?.Tags.Contains("云端") == true,
            $"本机笔记数={h.Notes.Notes.Count}");

        await r.Case("C08 云端较新版本覆盖本机（rev 变化走 UseRemote）", async () =>
        {
            var cloud = await h.ReadRemoteNote(n1.Id) ?? throw new Exception("云端笔记丢失");
            cloud.Rev = cloud.Rev + 1;
            cloud.Title = "C08 云端覆盖标题";
            cloud.UpdatedAt = DateTime.UtcNow.AddMinutes(3);
            await h.WriteRemoteNote(cloud);
            await h.Engine.QuickSyncAsync();
            var local = h.Notes.Find(n1.Id);
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && local?.Title == "C08 云端覆盖标题" && local.Rev == cloud.Rev,
                $"本机标题={local?.Title} rev={local?.Rev} 状态={h.Engine.Status.Kind} {h.Engine.LastMessage}");
        });

        // ── 本机 → 云端 ─────────────────────────────────────────────
        await r.Case("C09 本机修改上传且 rev 递增", async () =>
        {
            var before = await h.ReadRemoteNote(n2.Id) ?? throw new Exception("云端笔记丢失");
            h.Notes.UpdateTitle(n2.Id, "C09 本地改过的标题");
            h.Notes.UpdateContent(n2.Id, "<p>本地改写 <i>斜体</i></p>", NoteContentFormat.richText);
            await h.Engine.QuickSyncAsync();
            var after = await h.ReadRemoteNote(n2.Id);
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && after?.Title == "C09 本地改过的标题" && after.Rev > before.Rev
                    && (after.Body ?? "").Contains("斜体"),
                $"rev {before.Rev}→{after?.Rev} 标题={after?.Title} 状态={h.Engine.Status.Kind} {h.Engine.LastMessage}");
        });

        // ── 冲突 ────────────────────────────────────────────────────
        await r.Case("C10 双端同时修改生成冲突副本，云端新版留原 id", async () =>
        {
            h.Notes.UpdateTitle(n3.Id, "C10 本地冲突版本");
            var localTime = h.Notes.Find(n3.Id)!.UpdatedAt;
            var cloud = await h.ReadRemoteNote(n3.Id) ?? throw new Exception("云端笔记丢失");
            cloud.Rev += 1;
            cloud.Title = "C10 云端冲突版本";
            cloud.UpdatedAt = localTime.AddMinutes(5);
            await h.WriteRemoteNote(cloud);
            await h.Engine.QuickSyncAsync();

            var copy = h.Notes.Notes.FirstOrDefault(item => item.Title.Contains("（冲突副本）"));
            var winner = h.Notes.Find(n3.Id);
            var remoteCopy = copy == null ? null : await h.ReadRemoteNote(copy.Id);
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && copy != null && copy.Title.Contains("C10 本地冲突版本")
                    && winner?.Title == "C10 云端冲突版本" && remoteCopy != null,
                $"冲突副本={copy?.Title} 原 id 标题={winner?.Title} 副本已上云={remoteCopy != null} 状态={h.Engine.Status.Kind} {h.Engine.LastMessage}");
        });

        // ── 删除语义 ────────────────────────────────────────────────
        await r.Case("C11 移入回收站写 deletedAt 墓碑（不物理删云端文件）", async () =>
        {
            h.Notes.MoveToTrash(n1.Id);
            await h.Engine.QuickSyncAsync();
            var raw = await h.ReadRaw(h.NoteRemote(n1.Id));
            var cloud = TonoteJson.Deserialize<Note>(raw);
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && cloud?.DeletedAt != null && cloud.Purged == false,
                $"deletedAt={cloud?.DeletedAt:u} purged={cloud?.Purged}");
        });

        await r.Case("C12 彻底删除写 purged 墓碑且文件仍在", async () =>
        {
            h.Notes.PermanentlyDelete(n1.Id);
            await h.Engine.QuickSyncAsync();
            var cloud = await h.ReadRemoteNote(n1.Id);
            return (h.Engine.Status.Kind == SyncStatusKind.Success && cloud?.Purged == true,
                $"purged={cloud?.Purged} 状态={h.Engine.Status.Kind} {h.Engine.LastMessage}");
        });

        // ── 待办 ────────────────────────────────────────────────────
        var t1 = h.Todos.Add("C13 待办 A");
        var t2 = h.Todos.Add("C13 待办 B");
        h.Todos.Add("C13 待办 C");
        await h.SyncRound(r, "C13 三条待办上传成功");
        await r.Case("C14 待办按 UUID 落到 todos/ 且字段一致", async () =>
        {
            var entries = await h.ListTodos();
            var cloud = await h.ReadRemoteTodo(t1.Id);
            return (entries.Count == 3 && cloud?.Title == "C13 待办 A"
                    && cloud.Status == TodoStatus.todo && cloud.Purged == false,
                $"云端 {entries.Count} 项；t1={cloud?.Title}/{cloud?.Status}");
        });

        await r.Case("C15 待办状态变更与云端新增双向同步", async () =>
        {
            var remoteTodo = new Todo
            {
                Id = Guid.NewGuid(), Title = "C15 云端新增待办", Status = TodoStatus.doing,
                Rev = 1, Notes = "备注来自云端", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            await h.WriteRemoteTodo(remoteTodo);
            h.Todos.SetStatus(t2.Id, TodoStatus.done);
            await h.Engine.QuickSyncAsync();
            var localNew = h.Todos.Find(remoteTodo.Id);
            var cloudChanged = await h.ReadRemoteTodo(t2.Id);
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && localNew?.Title == "C15 云端新增待办"
                    && cloudChanged?.Status == TodoStatus.done,
                $"本机新增={localNew?.Title} 云端 t2 状态={cloudChanged?.Status} 状态={h.Engine.Status.Kind} {h.Engine.LastMessage}");
        });

        // ── 笔记本名单 ──────────────────────────────────────────────
        await r.Case("C16 笔记本名单上传与云端合并并集", async () =>
        {
            h.Notebooks.Add("C16 本机新建本");
            await h.Engine.QuickSyncAsync();
            var notebookPath = ctx.Remote("notebooks.json");
            var cloud = TonoteJson.Deserialize<NotebookCloud>(await h.ReadRaw(notebookPath));
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && cloud?.Names.Contains("C16 本机新建本") == true,
                $"云端名单=[{string.Join(",", cloud?.Names ?? [])}]");
        });

        await r.Case("C17 云端新增笔记本被合并到本机", async () =>
        {
            var notebookPath = ctx.Remote("notebooks.json");
            var cloud = TonoteJson.Deserialize<NotebookCloud>(await h.ReadRaw(notebookPath))
                        ?? new NotebookCloud();
            cloud.Names.Add("C17 云端新建本");
            await h.WriteRemoteRaw(notebookPath, TonoteJson.Serialize(cloud));
            await h.Engine.QuickSyncAsync();
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && h.Notebooks.Custom.Contains("C17 云端新建本")
                    && h.Notebooks.Custom.Contains("C16 本机新建本"),
                $"本机名单=[{string.Join(",", h.Notebooks.Custom)}]");
        });

        // ── 附件 ────────────────────────────────────────────────────
        var imageName = Guid.NewGuid().ToString("D").ToUpperInvariant() + ".png";
        var imageRel = $"attachments/{n2.Id.ToString("D").ToUpperInvariant()}/{imageName}";
        var imageBytes = new byte[4096 + 17];
        new Random(777).NextBytes(imageBytes);
        Encoding.ASCII.GetBytes("\x89PNG\r\n\x1a\n").CopyTo(imageBytes, 0);
        var imageLocal = h.Notes.Attachments.AbsolutePath(imageRel);
        Directory.CreateDirectory(Path.GetDirectoryName(imageLocal)!);
        await File.WriteAllBytesAsync(imageLocal, imageBytes);

        await r.Case("C18 正文引用的图片随笔记上传到 attachments/", async () =>
        {
            h.Notes.UpdateContent(n2.Id,
                $"<p>带图</p><img src=\"{imageRel}\" alt=\"测试图\">", NoteContentFormat.richText);
            await h.Engine.QuickSyncAsync();
            byte[]? cloud = null;
            try { cloud = await ctx.Client.DownloadDataAsync(ctx.Remote(imageRel), at); }
            catch (BaiduPanNotFoundException) { }
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && cloud != null && cloud.SequenceEqual(imageBytes),
                $"云端附件={(cloud == null ? "缺失" : cloud.Length + " 字节")} 一致={cloud?.SequenceEqual(imageBytes)} 状态={h.Engine.Status.Kind} {h.Engine.LastMessage}");
        });

        var remoteImageNoteId = Guid.NewGuid();
        var remoteImageName = Guid.NewGuid().ToString("D").ToUpperInvariant() + ".png";
        var remoteImageRel = $"attachments/{remoteImageNoteId.ToString("D").ToUpperInvariant()}/{remoteImageName}";
        var remoteImageBytes = new byte[2048 + 9];
        new Random(999).NextBytes(remoteImageBytes);
        await ctx.Client.UploadDataAsync(remoteImageBytes, ctx.Remote(remoteImageRel), at);
        await h.WriteRemoteNote(new Note
        {
            Id = remoteImageNoteId, Title = "C19 云端带图笔记",
            Body = $"<p>云端图</p><img src=\"{remoteImageRel}\">",
            ContentFormat = NoteContentFormat.richText, Rev = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await h.SyncRound(r, "C19 云端带图笔记同步轮成功");
        r.Check("C20 云端图片被下载到本机附件目录",
            h.Notes.Attachments.FileExists(remoteImageRel)
            && File.ReadAllBytes(h.Notes.Attachments.AbsolutePath(remoteImageRel))
                .SequenceEqual(remoteImageBytes),
            $"本机路径存在={h.Notes.Attachments.FileExists(remoteImageRel)}");

        // ── 单篇同步 ────────────────────────────────────────────────
        await r.Case("C21 SyncSingleNoteAsync 只同步指定一篇", async () =>
        {
            var before = (await h.ListNotes()).ToDictionary(item => item.Name, item => item.ServerMtime);
            h.Notes.UpdateTitle(n3.Id, "C21 单篇同步标题");
            await h.Engine.SyncSingleNoteAsync(n3.Id);
            var after = (await h.ListNotes()).ToDictionary(item => item.Name, item => item.ServerMtime);
            var target = SyncHarness.FileName(n3.Id);
            var others = before.Keys.Where(key => key != target)
                .Where(key => after.TryGetValue(key, out var nowMtime) && nowMtime != before[key])
                .ToArray();
            var cloud = await h.ReadRemoteNote(n3.Id);
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && cloud?.Title == "C21 单篇同步标题" && others.Length == 0,
                $"目标标题={cloud?.Title} 其它被改动=[{string.Join(",", others)}] 状态={h.Engine.Status.Kind} {h.Engine.LastMessage}");
        });

        // ── 快同步笔记 ──────────────────────────────────────────────
        await r.Case("C22 快同步笔记不参与常规轮，单篇触发可上传", async () =>
        {
            var quick = new Note
            {
                Id = Note.QuickSyncNoteId, Title = "C22 快同步专属笔记", Body = "随手记",
                ContentFormat = NoteContentFormat.markdown,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            h.Notes.SetNotes([.. h.Notes.Notes, quick]);
            await h.Engine.QuickSyncAsync();
            var absent = (await h.ListNotes()).All(item => item.Name != SyncHarness.FileName(quick.Id));
            await h.Engine.SyncSingleNoteAsync(quick.Id);
            var present = (await h.ListNotes()).Any(item => item.Name == SyncHarness.FileName(quick.Id));
            return (absent && present,
                $"常规轮未上传={absent} 单篇触发后存在={present} 状态={h.Engine.Status.Kind}");
        });

        r.Check("C23 同步状态与 last-sync.json 已落盘",
            h.Engine.LastSyncDate != null && h.Engine.Status.Kind == SyncStatusKind.Success
            && File.Exists(Path.Combine(ctx.LocalRoot, "last-sync.json")),
            $"上次同步={h.Engine.LastSyncDate:u} 状态={h.Engine.Status.Kind}");

        // ── 旧 manifest 迁移（放最后，会重置云端 notes 目录） ────────
        await r.Case("C24 旧 manifest 拆成按篇文件", async () =>
        {
            foreach (var entry in await h.ListNotes())
                if (entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    await ctx.Client.DeleteRemoteAsync(ctx.Remote("notes/" + entry.Name), at);

            var old1 = Guid.NewGuid();
            var old2 = Guid.NewGuid();
            var oldTodo = Guid.NewGuid();
            var manifest = new
            {
                notes = new[]
                {
                    new Note { Id = old1, Title = "旧清单笔记 1", Body = "manifest 1", Rev = 2,
                        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                    new Note { Id = old2, Title = "旧清单笔记 2", Body = "manifest 2", Rev = 1,
                        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                },
                todos = new[]
                {
                    new Todo { Id = oldTodo, Title = "旧清单待办", Rev = 1,
                        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                },
            };
            await h.WriteRemoteRaw(ctx.Remote("manifest.json"), TonoteJson.Serialize(manifest));
            await h.Engine.QuickSyncAsync();
            var entries = await h.ListNotes();
            var names = entries.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (h.Engine.Status.Kind == SyncStatusKind.Success
                    && names.Contains(SyncHarness.FileName(old1))
                    && names.Contains(SyncHarness.FileName(old2)),
                $"迁移后 notes/{entries.Count} 项，含旧两篇={names.Contains(SyncHarness.FileName(old1)) && names.Contains(SyncHarness.FileName(old2))} 状态={h.Engine.Status.Kind} {h.Engine.LastMessage}");
        });
    }
}
