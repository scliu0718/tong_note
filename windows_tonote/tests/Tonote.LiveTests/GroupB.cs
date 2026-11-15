using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Tonote.Core;

namespace Tonote.LiveTests;

/// <summary>B 组：真实百度网盘的文件级原语，全部落在本次 run 的沙箱子目录。</summary>
public static class GroupB
{
    public static async Task RunAsync(LiveContext ctx, Runner r)
    {
        r.Section("B 网盘原语（真实上传 / 下载 / 列举 / 删除）");
        var at = ctx.AccessToken;

        var smallPath = ctx.Remote("notes/b01.json");
        var small = Encoding.UTF8.GetBytes("{\"id\":\"b01\",\"title\":\"真实上传测试\"}");

        await r.Case("B01 上传小 JSON（自动建父目录）", async () =>
        {
            await ctx.Client.UploadDataAsync(small, smallPath, at);
            ctx.CreatedRemotePaths.Add(smallPath);
            return (true, $"{small.Length} 字节 → {smallPath}");
        });

        await r.Case("B02 列举 notes 目录能找到文件且元数据正确", async () =>
        {
            var entries = await ctx.Client.ListFilesAsync(ctx.Remote("notes"), at);
            var hit = entries.FirstOrDefault(item => item.Name == "b01.json");
            return (hit != null && hit.Size == small.Length && hit.ServerMtime > 0,
                $"共 {entries.Count} 项；命中={(hit == null ? "无" : $"size={hit.Size} mtime={hit.ServerMtime} isdir={hit.IsDir}")}");
        });

        await r.Case("B03 下载字节完全一致", async () =>
        {
            var data = await ctx.Client.DownloadDataAsync(smallPath, at);
            return (data.SequenceEqual(small), $"{data.Length} 字节，一致={data.SequenceEqual(small)}");
        });

        var replaced = Encoding.UTF8.GetBytes("{\"id\":\"b01\",\"title\":\"覆盖写\",\"extra\":[1,2,3]}");
        await r.Case("B04 同路径覆盖写（rtype=3）", async () =>
        {
            await ctx.Client.UploadDataAsync(replaced, smallPath, at);
            var data = await ctx.Client.DownloadDataAsync(smallPath, at);
            var entries = await ctx.Client.ListFilesAsync(ctx.Remote("notes"), at);
            var hit = entries.FirstOrDefault(item => item.Name == "b01.json");
            return (data.SequenceEqual(replaced) && hit?.Size == replaced.Length,
                $"新长度={data.Length} 云端 size={hit?.Size} 一致={data.SequenceEqual(replaced)}");
        });

        var chinesePath = ctx.Remote("notes/中文笔记.json");
        var chinese = Encoding.UTF8.GetBytes("中文内容：同笔记跨端数据契约 ✓ 表格 <td data-align=\"center\">");
        await r.Case("B05 中文文件名与中文内容往返", async () =>
        {
            await ctx.Client.UploadDataAsync(chinese, chinesePath, at);
            var data = await ctx.Client.DownloadDataAsync(chinesePath, at);
            var entries = await ctx.Client.ListFilesAsync(ctx.Remote("notes"), at);
            return (data.SequenceEqual(chinese) && entries.Any(item => item.Name == "中文笔记.json"),
                $"{data.Length} 字节，列举到中文名={entries.Any(item => item.Name == "中文笔记.json")}");
        });

        var binaryPath = ctx.Remote("attachments/b06.png");
        var binary = new byte[64 * 1024 + 37];
        new Random(20261115).NextBytes(binary);
        Encoding.ASCII.GetBytes("\x89PNG\r\n\x1a\n").CopyTo(binary, 0);
        var binaryMd5 = Convert.ToHexString(MD5.HashData(binary));
        await r.Case("B06 二进制附件往返（PNG 头 + 随机字节）", async () =>
        {
            await ctx.Client.UploadDataAsync(binary, binaryPath, at);
            var data = await ctx.Client.DownloadDataAsync(binaryPath, at);
            var md5 = Convert.ToHexString(MD5.HashData(data));
            return (md5 == binaryMd5, $"{data.Length} 字节 md5={md5[..12]}… 一致={md5 == binaryMd5}");
        });

        var big = new byte[9 * 1024 * 1024 + 123];
        new Random(42).NextBytes(big);
        var bigMd5 = Convert.ToHexString(MD5.HashData(big));
        var bigPath = ctx.Remote("attachments/b07_large.bin");
        var slices = UploadChunks.Slices(big);
        r.Check("B07 9MB 数据切成 3 片（4MB 分片规则）", slices.Count == 3,
            $"片数={slices.Count} 各片={string.Join(",", slices.Select(s => s.Length))}");

        await r.Case("B08 多分片上传真实成功", async () =>
        {
            var sw = Stopwatch.StartNew();
            await ctx.Client.UploadDataAsync(big, bigPath, at);
            sw.Stop();
            return (true, $"{big.Length} 字节 / {slices.Count} 片，耗时 {sw.Elapsed.TotalSeconds:0.0}s");
        });

        await r.Case("B09 大文件下载字节一致", async () =>
        {
            var data = await ctx.Client.DownloadDataAsync(bigPath, at);
            var md5 = Convert.ToHexString(MD5.HashData(data));
            return (md5 == bigMd5, $"{data.Length} 字节 md5 一致={md5 == bigMd5}");
        });

        await r.Case("B10 重复内容二次上传（秒传 / return_type=2 分支）", async () =>
        {
            await ctx.Client.UploadDataAsync(big, bigPath, at);
            var data = await ctx.Client.DownloadDataAsync(bigPath, at);
            return (Convert.ToHexString(MD5.HashData(data)) == bigMd5,
                $"重复上传后 {data.Length} 字节仍一致");
        });

        await r.Case("B11 删除远端文件后列举消失", async () =>
        {
            await ctx.Client.DeleteRemoteAsync(smallPath, at);
            var entries = await ctx.Client.ListFilesAsync(ctx.Remote("notes"), at);
            return (entries.All(item => item.Name != "b01.json"),
                $"剩余 {entries.Count} 项：{string.Join(",", entries.Select(item => item.Name).Take(6))}");
        });

        await r.Case("B12 删除不存在的远端文件不报错", async () =>
        {
            await ctx.Client.DeleteRemoteAsync(ctx.Remote("notes/__never_exists__.json"), at);
            return (true, "按 Mac 版语义视为成功");
        });

        await r.Case("B13 EnsureRemoteDirAsync 可创建应用沙箱目录", async () =>
        {
            await ctx.Client.EnsureRemoteDirAsync(at);
            var entries = await ctx.Client.ListFilesAsync(ctx.RemoteRoot, at);
            return (entries.Count > 0, $"沙箱根 {entries.Count} 项：{string.Join(",", entries.Select(item => item.Name).Take(8))}");
        });

        await r.Case("B14 空文件上传（0 字节边界）", async () =>
        {
            var emptyPath = ctx.Remote("notes/b14_empty.bin");
            try
            {
                await ctx.Client.UploadDataAsync([], emptyPath, at);
                var data = await ctx.Client.DownloadDataAsync(emptyPath, at);
                return (data.Length == 0, "0 字节上传下载成功");
            }
            catch (BaiduPanException ex)
            {
                return (false, "百度拒绝 0 字节：" + ex.Message);
            }
        });
    }
}
