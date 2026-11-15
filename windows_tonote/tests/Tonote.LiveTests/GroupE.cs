using System.Text;
using System.Text.Json;
using Tonote.Core;

namespace Tonote.LiveTests;

/// <summary>
/// E 组：拿生产目录里 Mac / Android 端真实写出的笔记，验证 Windows 协议层能无损读取并回写。
/// 这是「与 Mac 版功能对齐」最直接的证据。
/// </summary>
public static class GroupE
{
    public static async Task RunAsync(LiveContext ctx, Runner r)
    {
        r.Section("E 跨端协议（Mac 生产真实数据 → Windows 解码）");
        var at = ctx.AccessToken;
        const string prod = BaiduPanConfig.DefaultRemoteRoot;

        async Task<string?> ReadProd(string path)
        {
            try
            {
                return Encoding.UTF8.GetString(await ctx.Client.DownloadDataAsync(path, at));
            }
            catch (BaiduPanException)
            {
                // 生产目录是多端实时共享的：列举之后某篇可能被别的端删掉，或直链失效。
                // 单篇读不到只计入 skipped，不能让整套回归崩掉。
                return null;
            }
        }

        List<BaiduPanListEntry> entries;
        try { entries = await ctx.Client.ListFilesAsync(prod + "/notes", at); }
        catch (BaiduPanNotFoundException) { entries = []; }
        var names = entries.Where(item => item.IsDir == 0
                                          && item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name).Take(12).ToList();

        r.Check("E01 生产 notes/ 可读且存在按篇文件", names.Count > 0,
            $"列举 {entries.Count} 项，取前 {names.Count} 篇");

        var notes = new List<(string File, string Raw, Note Note)>();
        var skipped = 0;
        foreach (var name in names)
        {
            var raw = await ReadProd(prod + "/notes/" + name);
            if (raw == null) { skipped++; continue; }
            try
            {
                var note = TonoteJson.Deserialize<Note>(raw);
                if (note != null) notes.Add((name, raw, note));
                else skipped++;
            }
            catch { skipped++; /* 单篇解析失败计入统计 */ }
        }
        r.Check("E02 Windows 能解析 Mac 端按篇笔记", notes.Count > 0 && skipped <= 2,
            $"成功 {notes.Count}/{names.Count} 篇，跳过 {skipped} 篇（云端条目实时变动）");

        r.Check("E03 文件名 UUID 与正文 id 一致、时间可解析",
            notes.All(item => Guid.TryParse(item.File[..^5], out var fileId) && fileId == item.Note.Id
                              && item.Note.UpdatedAt.Year >= 2020),
            $"校验 {notes.Count} 篇；示例 id={notes.FirstOrDefault().Note.Id}");

        var rich = notes.Where(item => item.Note.ContentFormat == NoteContentFormat.richText
                                       && !string.IsNullOrWhiteSpace(item.Note.Body)).ToList();
        await r.Case("E04 Mac 富文本 HTML → Windows 解码 → 编码 → 再解码幂等", async () =>
        {
            await Task.Yield();
            if (rich.Count == 0) return (true, "生产目录暂无富文本笔记，跳过（不视为失败）");
            var problems = new List<string>();
            foreach (var item in rich)
            {
                var first = RichTextHtmlCodec.Decode(item.Note.Body);
                var encoded = RichTextHtmlCodec.Encode(first);
                var second = RichTextHtmlCodec.Decode(encoded);
                if (first.Blocks.Count != second.Blocks.Count)
                    problems.Add($"{item.File[..8]}: 块数 {first.Blocks.Count}→{second.Blocks.Count}");
                if (first.Blocks.Count(b => b.Table != null) != second.Blocks.Count(b => b.Table != null))
                    problems.Add($"{item.File[..8]}: 表格数不一致");
            }
            return (problems.Count == 0,
                $"富文本 {rich.Count} 篇，块数示例={RichTextHtmlCodec.Decode(rich[0].Note.Body).Blocks.Count} 问题=[{string.Join(";", problems)}]");
        });

        r.Check("E05 Windows 重编码后仍能还原 Mac 正文的关键内容",
            rich.Count == 0 || rich.All(item =>
            {
                var plain = MarkdownConverter.PlainText(item.Note.Body, NoteContentFormat.richText);
                var rebuilt = RichTextHtmlCodec.Encode(RichTextHtmlCodec.Decode(item.Note.Body));
                var rebuiltPlain = MarkdownConverter.PlainText(rebuilt, NoteContentFormat.richText);
                return string.IsNullOrWhiteSpace(plain)
                       || plain.Trim() == rebuiltPlain.Trim()
                       || rebuiltPlain.Contains(plain.Trim()[..Math.Min(8, plain.Trim().Length)]);
            }),
            $"富文本 {rich.Count} 篇纯文本比对通过");

        var markdown = notes.Where(item => item.Note.ContentFormat == NoteContentFormat.markdown
                                           && !string.IsNullOrWhiteSpace(item.Note.Body)).ToList();
        r.Check("E06 Mac Markdown 笔记可被 Windows 转换器处理",
            markdown.Count == 0 || markdown.All(item =>
                MarkdownConverter.ToDocument(item.Note.Body).Blocks.Count > 0
                && !string.IsNullOrWhiteSpace(MarkdownConverter.PlainTextFromMarkdown(item.Note.Body))),
            $"Markdown {markdown.Count} 篇");

        r.Check("E07 Note 展示属性对真实数据可用（Snippet / 字数 / 时间）",
            notes.All(item => !string.IsNullOrWhiteSpace(item.Note.Snippet)
                              && item.Note.WordCount >= 0
                              && !string.IsNullOrWhiteSpace(item.Note.TimeText)),
            $"示例标题={notes.FirstOrDefault().Note.Title} 摘要={notes.FirstOrDefault().Note.Snippet}");

        await r.Case("E08 生产 manifest.json / avatar.json / notebooks.json 结构兼容", async () =>
        {
            var manifest = await ReadProd(prod + "/manifest.json");
            var avatar = await ReadProd(prod + "/avatar.json");
            var notebooks = await ReadProd(prod + "/notebooks.json");
            var manifestOk = manifest == null || (JsonDocument.Parse(manifest).RootElement
                .TryGetProperty("notes", out var notesArray) && notesArray.ValueKind == JsonValueKind.Array);
            var avatarOk = avatar == null || (JsonDocument.Parse(avatar).RootElement
                .TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String);
            var notebookOk = notebooks == null ||
                             TonoteJson.Deserialize<NotebookCloud>(notebooks) != null;
            return (manifestOk && avatarOk && notebookOk,
                $"manifest={manifestOk} avatar={avatarOk} notebooks={notebookOk}（存在才校验）");
        });

        await r.Case("E09 生产 avatar.json 的预设 id 在 Windows 八宫格中存在", async () =>
        {
            var avatar = await ReadProd(prod + "/avatar.json");
            if (avatar == null) return (true, "云端未设置头像，跳过");
            var id = JsonDocument.Parse(avatar).RootElement.GetProperty("id").GetString();
            return (AvatarStore.Presets.Any(preset => preset.Id == id),
                $"Mac 选择={id} Windows 存在={AvatarStore.Presets.Any(preset => preset.Id == id)}");
        });

        await r.Case("E10 生产设备文件可被 Windows DeviceRecord 解析", async () =>
        {
            List<BaiduPanListEntry> devices;
            try { devices = await ctx.Client.ListFilesAsync(prod + "/devices", at); }
            catch (BaiduPanNotFoundException) { return (true, "生产无 devices 目录，跳过"); }
            var file = devices.FirstOrDefault(item => item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            if (file == null) return (true, "生产无设备文件，跳过");
            var raw = await ReadProd(file.Path ?? prod + "/devices/" + file.Name);
            var record = raw == null ? null : TonoteJson.Deserialize<DeviceRecord>(raw);
            return (record != null && DeviceRecord.IsSafeDeviceId(record.DeviceId),
                $"设备={record?.DeviceName} id={record?.DeviceId}");
        });

        r.Check("E11 受控 HTML 表格 / 图片 / 对齐属性跨端往返",
            CheckTableRoundTrip(), "table[data-fullwidth] + td[data-align|data-bg] + img[src=attachments/…]");

        var now = DateTime.UtcNow;
        var cursor = new SyncCursor { BaseRev = 2, BaseUpdatedAt = now, ServerMtime = 100, Dirty = false };
        r.Check("E12 SyncRules 五分支与分片规则回归",
            SyncRules.AfterFetch(true, null, null, now, SyncCursor.Unsynced()).Kind == SyncStepKind.Upload
            && SyncRules.AfterFetch(false, 3, now, null, SyncCursor.Unsynced()).Kind == SyncStepKind.UseRemote
            && SyncRules.AfterFetch(true, 3, now, now.AddMinutes(5), SyncCursor.Unsynced()).Kind == SyncStepKind.Conflict
            && SyncRules.AfterFetch(false, 2, now, now, cursor).Kind == SyncStepKind.RememberMtime
            && SyncRules.AfterFetch(true, 2, now, now, cursor) == SyncStep.Upload(3)
            && SyncRules.ShouldSkipFetch(100, cursor) && !SyncRules.ShouldSkipFetch(101, cursor)
            && UploadChunks.Slices(new byte[9 * 1024 * 1024]).Count == 3
            && UploadChunks.Slices([]).Count == 1,
            "Upload / UseRemote / Conflict / RememberMtime / 4MB 分片 / 0 字节边界");

        r.Check("E13 笔记本名单三方合并与附件路径提取",
            NotebookMerge.Merge(["本地本"], ["已删本"], new NotebookCloud
            {
                Names = ["本地本", "云端本"], Removed = ["已删本"],
            }).Names.SequenceEqual(new[] { "云端本", "本地本" }.OrderBy(x => x, StringComparer.CurrentCulture))
            && NoteAttachmentIndex.RelativePaths(
                "<img src=\"attachments/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222.png\">")
                .Count == 1,
            "名单取并集且排除 removed，图片相对路径去重");
    }

    private static bool CheckTableRoundTrip()
    {
        const string html = "<p>前文</p>"
                            + "<table data-fullwidth=\"true\" data-widths=\"0.6,0.4\">"
                            + "<tbody><tr><td data-align=\"center\" data-bg=\"#AABBCC\"><p>A1</p></td>"
                            + "<td data-align=\"right\"><p>B1</p></td></tr></tbody></table>"
                            + "<p><img src=\"attachments/11111111-1111-1111-1111-111111111111/"
                            + "22222222-2222-2222-2222-222222222222.png\"></p>";
        var doc = RichTextHtmlCodec.Decode(html);
        var table = doc.Blocks.FirstOrDefault(block => block.Table != null)?.Table;
        var encoded = RichTextHtmlCodec.Encode(doc);
        var again = RichTextHtmlCodec.Decode(encoded);
        var table2 = again.Blocks.FirstOrDefault(block => block.Table != null)?.Table;
        return table != null && table2 != null
               && table.Rows.Count == 1 && table.Rows[0].Count == 2
               && table.Rows[0][0].BackgroundHex == "#AABBCC"
               && table.Rows[0][0].Alignment == RichTextCellAlignment.center
               && table2.Rows[0].Count == 2
               && encoded.Contains("attachments/11111111-1111-1111-1111-111111111111/");
    }
}
