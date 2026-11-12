namespace Tonote.Core;

public enum NoteContentFormat
{
    markdown,
    richText,
}

/// <summary>正文和格式必须一起写入，避免出现 HTML 正文配 markdown 格式。</summary>
public sealed class NoteContent
{
    public string Body { get; set; } = "";
    public NoteContentFormat Format { get; set; } = NoteContentFormat.markdown;

    public NoteContent()
    {
    }

    public NoteContent(string body, NoteContentFormat format)
    {
        Body = body;
        Format = format;
    }
}
