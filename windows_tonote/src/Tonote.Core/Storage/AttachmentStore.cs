namespace Tonote.Core;

public sealed class AttachmentException : Exception
{
    public AttachmentException(string message) : base(message) { }
}

/// <summary>
/// 图片附件仓库。目录结构与 Mac / Android 完全一致：
/// <root>/attachments/<note-uuid>/<image-uuid>.<ext>
/// 正文里只保存正斜杠的相对路径，因此三端同步后仍能指向同一份附件。
/// </summary>
public sealed class AttachmentStore
{
    public static readonly HashSet<string> AllowedExtensions =
    [
        "png", "jpg", "jpeg", "gif", "webp", "heic", "heif", "tiff", "tif", "bmp"
    ];

    private readonly string _root;

    public AttachmentStore(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
        AttachmentsDirectory = Path.Combine(_root, "attachments");
        Directory.CreateDirectory(AttachmentsDirectory);
    }

    public string RootDirectory => _root;
    public string AttachmentsDirectory { get; }

    public string DirectoryFor(Guid noteId)
        => Path.Combine(AttachmentsDirectory, noteId.ToString("D").ToUpperInvariant());

    /// <summary>导入一张图片：复制到附件目录并返回相对路径（始终用 / 分隔）。</summary>
    public string ImportImage(string sourcePath, Guid noteId)
    {
        if (!File.Exists(sourcePath))
            throw new AttachmentException($"找不到图片文件：{sourcePath}");

        var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            throw new AttachmentException(
                $"不支持的图片格式：{(ext.Length == 0 ? "未知" : ext)}（支持 png / jpg / gif / webp / heic / tiff / bmp）");

        var targetDirectory = DirectoryFor(noteId);
        Directory.CreateDirectory(targetDirectory);
        var fileName = Guid.NewGuid().ToString("D").ToUpperInvariant() + "." + ext;
        var target = Path.Combine(targetDirectory, fileName);
        try
        {
            File.Copy(sourcePath, target, overwrite: false);
        }
        catch (Exception ex)
        {
            throw new AttachmentException("图片写入失败：" + ex.Message);
        }

        return RelativePath(target);
    }

    /// <summary>
    /// 从内存字节导入一张图片（data: URI 解码后的粘贴图走这里），返回相对路径。
    /// 与 ImportImage 共用同一目录约定，保证三端同步路径一致。
    /// </summary>
    public string ImportImageBytes(byte[] bytes, string ext, Guid noteId)
    {
        if (bytes == null || bytes.Length == 0)
            throw new AttachmentException("图片内容为空");
        var normalized = (ext ?? "").TrimStart('.').ToLowerInvariant();
        if (!AllowedExtensions.Contains(normalized))
            throw new AttachmentException($"不支持的图片格式：{(normalized.Length == 0 ? "未知" : normalized)}");

        var targetDirectory = DirectoryFor(noteId);
        Directory.CreateDirectory(targetDirectory);
        var fileName = Guid.NewGuid().ToString("D").ToUpperInvariant() + "." + normalized;
        var target = Path.Combine(targetDirectory, fileName);
        try
        {
            File.WriteAllBytes(target, bytes);
        }
        catch (Exception ex)
        {
            throw new AttachmentException("图片写入失败：" + ex.Message);
        }

        return RelativePath(target);
    }

    /// <summary>由绝对路径推出相对路径。目标不在 root 下时退化为文件名。</summary>
    public string RelativePath(string absolutePath)
    {
        var target = Path.GetFullPath(absolutePath).Replace('\\', '/');
        var basePath = _root.Replace('\\', '/').TrimEnd('/');
        if (target.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return target[(basePath.Length + 1)..];
        return Path.GetFileName(absolutePath);
    }

    /// <summary>由相对路径还原绝对路径。</summary>
    public string AbsolutePath(string relativePath)
    {
        var trimmed = relativePath.Replace('\\', '/').TrimStart('/');
        return Path.Combine(_root, trimmed.Replace('/', Path.DirectorySeparatorChar));
    }

    public bool FileExists(string relativePath) => File.Exists(AbsolutePath(relativePath));

    /// <summary>删除一篇笔记的全部附件（彻底删除笔记时调用）。</summary>
    public void RemoveAttachments(Guid noteId)
    {
        var target = DirectoryFor(noteId);
        if (Directory.Exists(target))
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch
            {
                // 与 Mac 版一致：清理失败不阻断删除流程
            }
        }
    }

    public (int Count, long Bytes) Usage(Guid noteId)
    {
        var target = DirectoryFor(noteId);
        if (!Directory.Exists(target))
            return (0, 0);
        try
        {
            var files = Directory.GetFiles(target);
            return (files.Length, files.Sum(file => new FileInfo(file).Length));
        }
        catch
        {
            return (0, 0);
        }
    }
}
