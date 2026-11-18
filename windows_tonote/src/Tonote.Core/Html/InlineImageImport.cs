using System;
using System.IO;

namespace Tonote.Core;

/// <summary>
/// W-10 ③：把受控 HTML 里不允许的图片引用（data: / blob: / file:/// / 本地绝对路径）
/// 在写盘前转存为 attachments/&lt;noteUUID&gt;/&lt;imageUUID&gt;.&lt;ext&gt; 相对路径。
/// 无法转存的（如 blob: 需要浏览器侧解析）返回失败，由调用方整篇拒写并报错，
/// 绝不把自由 URI 写进云端 JSON（需求 6.3 第 6 条 / 12.6 第 2 项）。
/// </summary>
public static class InlineImageImport
{
    public enum Outcome { AlreadyRelative, Imported, Failed }

    public sealed record Result(Outcome Kind, string Path, string Error = "");

    /// <summary>判断一个 src 是否符合受控契约（只存相对路径）。</summary>
    public static bool IsAllowed(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var value = src.Replace('\\', '/');
        return value.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 转存一张内联图片。store 为 null 时只做识别：data/file 视为可转存但没有落点 → Failed，
    /// 便于单测在无文件系统时验证判定。
    /// </summary>
    public static Result Import(string src, Guid noteId, AttachmentStore? store)
    {
        if (IsAllowed(src))
            return new Result(Outcome.AlreadyRelative, src.Replace('\\', '/'));

        var value = (src ?? "").Trim();
        if (value.Length == 0)
            return new Result(Outcome.Failed, "", "图片地址为空");

        try
        {
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return ImportDataUri(value, noteId, store);

            if (value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                return new Result(Outcome.Failed, "",
                    "临时图片（blob:）无法直接保存，请在编辑器里重新粘贴图片文件");

            if (value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                var local = value["file:///".Length..];
                local = Uri.UnescapeDataString(local).Replace('/', Path.DirectorySeparatorChar);
                return ImportFile(local, noteId, store);
            }

            // 绝对路径：Windows 盘符 C:\... 或 UNC \\server\...，以及 Unix /...
            if (Path.IsPathRooted(value))
                return ImportFile(value, noteId, store);

            // http(s) 等远程 URL 不属于受控正文，不得写盘
            if (Uri.TryCreate(value, UriKind.Absolute, out _))
                return new Result(Outcome.Failed, "", $"不支持的图片引用：{value}");

            return new Result(Outcome.Failed, "", $"不认识的图片路径：{value}");
        }
        catch (Exception error)
        {
            return new Result(Outcome.Failed, "", "图片转存失败：" + error.Message);
        }
    }

    private static Result ImportDataUri(string value, Guid noteId, AttachmentStore? store)
    {
        var comma = value.IndexOf(',');
        if (comma < 0)
            return new Result(Outcome.Failed, "", "data: 图片格式不正确");
        var header = value[..comma];
        var payload = value[(comma + 1)..];

        var mimeEnd = header.IndexOf(';');
        var mime = mimeEnd > 5 ? header[5..mimeEnd] : header[5..];
        var ext = mime.ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" or "image/jpg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/bmp" => "bmp",
            "image/tiff" => "tiff",
            _ => "",
        };
        if (ext.Length == 0)
            return new Result(Outcome.Failed, "", $"不支持的内联图片格式：{mime}");
        if (store == null)
            return new Result(Outcome.Failed, "", "无附件存储，无法转存 data: 图片");

        byte[] bytes;
        try
        {
            bytes = header.Contains("base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (FormatException)
        {
            return new Result(Outcome.Failed, "", "data: 图片的 base64 内容无效");
        }

        return new Result(Outcome.Imported, store.ImportImageBytes(bytes, ext, noteId));
    }

    private static Result ImportFile(string path, Guid noteId, AttachmentStore? store)
    {
        if (!File.Exists(path))
            return new Result(Outcome.Failed, "", $"找不到图片文件：{path}");
        if (store == null)
            return new Result(Outcome.Failed, "", "无附件存储，无法转存本地图片");
        return new Result(Outcome.Imported, store.ImportImage(path, noteId));
    }
}
