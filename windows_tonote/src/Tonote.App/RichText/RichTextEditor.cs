using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tonote.Core;

namespace Tonote.App;

/// <summary>
/// 富文本编辑区：Avalonia 工具条 + WebView2 contenteditable。
/// 所有保存都经 RichTextHtmlCodec 归一化，保证写进 notes.json 的是三端受控 HTML。
/// WebView2 运行时缺失时自动退回纯文本框，不让编辑功能整体失效。
/// </summary>
public sealed class RichTextEditor : UserControl
{
    private readonly AppSession _session;
    private readonly WebView2Host _host;
    private readonly TextBox _fallback;
    private readonly WrapPanel _toolbar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _status = new()
    {
        FontSize = 11,
        Foreground = Palette.TextTertiary,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
    };
    private readonly TextBox _linkInput = new() { Watermark = "https://…", Width = 150, FontSize = 12 };
    private readonly ComboBox _fontSize = new() { Width = 74, FontSize = 12, SelectedIndex = 2 };

    private Guid _currentId;
    private string _currentBody = "";
    private NoteContentFormat _currentFormat = NoteContentFormat.markdown;
    private string? _pendingHtml;
    private string? _lastHtml;
    private bool _readOnly;
    private bool _webFailed;
    private CancellationTokenSource? _saveDelay;

    public event Action<string>? StatusChanged;

    public RichTextEditor(AppSession session)
    {
        _session = session;
        Directory.CreateDirectory(session.Root);
        var htmlPath = Path.Combine(session.Root, "editor.html");
        File.WriteAllText(htmlPath, EditorHtml.Content);

        _host = new WebView2Host(Path.Combine(session.Root, "webview2"))
        {
            MinHeight = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _fallback = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 15,
            MinHeight = 300,
            IsVisible = false,
        };
        _fallback.TextChanged += (_, _) => SaveFallback();

        _host.WebMessageReceived += OnWebMessage;
        _host.Ready += OnReady;
        _host.Failed += OnFailed;

        _fontSize.ItemsSource = new List<string> { "12", "14", "16", "18", "24", "32" };
        _fontSize.SelectionChanged += (_, _) =>
        {
            if (_fontSize.SelectedItem is not string text || !int.TryParse(text, out var px))
                return;
            var size = px <= 12 ? "1" : px <= 14 ? "2" : px <= 16 ? "3" : px <= 18 ? "4" : px <= 24 ? "5" : px <= 32 ? "6" : "7";
            Run("exec", "fontSize", size);
        };

        BuildToolbar();

        var surface = new Grid { Children = { _host, _fallback } };
        var page = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_toolbar, Dock.Top);
        page.Children.Add(_toolbar);
        page.Children.Add(surface);
        Content = page;

        _host.Navigate(new Uri(htmlPath).AbsoluteUri);
    }

    public bool IsWebViewReady => _host.IsReady;

    public Guid CurrentNoteId => _currentId;

    private void BuildToolbar()
    {
        foreach (var (label, tag) in new[] { ("正文", "p"), ("标题1", "h1"), ("标题2", "h2"), ("标题3", "h3"), ("引用", "blockquote"), ("代码", "pre") })
        {
            var value = tag;
            _toolbar.Children.Add(ToolButton(label, () => Run("block", value)));
        }

        _toolbar.Children.Add(ToolButton("B", () => Run("exec", "bold")));
        _toolbar.Children.Add(ToolButton("I", () => Run("exec", "italic")));
        _toolbar.Children.Add(ToolButton("U", () => Run("exec", "underline")));
        _toolbar.Children.Add(ToolButton("S", () => Run("exec", "strikeThrough")));
        _toolbar.Children.Add(_fontSize);
        _toolbar.Children.Add(ToolButton("字色", () => Run("exec", "foreColor", "#D03050")));
        _toolbar.Children.Add(ToolButton("底色", () => Run("exec", "hiliteColor", "#FFF2A8")));
        _toolbar.Children.Add(ToolButton("项目符号", () => Run("exec", "insertUnorderedList")));
        _toolbar.Children.Add(ToolButton("编号", () => Run("exec", "insertOrderedList")));

        _toolbar.Children.Add(_linkInput);
        _toolbar.Children.Add(ToolButton("加链接", () =>
        {
            var url = (_linkInput.Text ?? "").Trim();
            if (url.Length == 0) return;
            Run("insertLink", url, "");
            _linkInput.Text = "";
        }));
        _toolbar.Children.Add(ToolButton("插图", InsertImage));

        _toolbar.Children.Add(ToolButton("表格 3×3", () => Run("insertTable", "3", "3")));
        _toolbar.Children.Add(ToolButton("加行", () => Run("insertRow")));
        _toolbar.Children.Add(ToolButton("加列", () => Run("insertColumn")));
        _toolbar.Children.Add(ToolButton("删行", () => Run("removeRow")));
        _toolbar.Children.Add(ToolButton("删列", () => Run("removeColumn")));
        _toolbar.Children.Add(ToolButton("左对齐", () => Run("setCellAlign", "left")));
        _toolbar.Children.Add(ToolButton("居中", () => Run("setCellAlign", "center")));
        _toolbar.Children.Add(ToolButton("右对齐", () => Run("setCellAlign", "right")));
        _toolbar.Children.Add(ToolButton("单元格底色", () => Run("setCellBackground", "#F2F2F4")));
        _toolbar.Children.Add(_status);
    }

    private static Button ToolButton(string label, Action click)
    {
        var button = new Button
        {
            Content = label,
            FontSize = 12,
            Padding = new Thickness(8, 4),
            Margin = new Thickness(2, 2),
            Foreground = Palette.TextSecondary,
            Background = Palette.WindowBg,
            CornerRadius = new CornerRadius(5),
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>只在笔记切换或外部同步改了正文时刷新编辑区，避免打断输入。</summary>
    public void LoadNote(Note note)
    {
        if (note.Id == _currentId && note.Body == _currentBody && note.ContentFormat == _currentFormat)
        {
            SetReadOnly(note.IsTrashed);
            return;
        }

        _currentId = note.Id;
        _currentBody = note.Body;
        _currentFormat = note.ContentFormat;
        _pendingHtml = note.ContentFormat == NoteContentFormat.richText
            ? note.Body
            : RichTextHtmlCodec.Encode(MarkdownConverter.ToDocument(note.Body));

        _loadingFallback = true;
        _fallback.Text = note.VisibleBody;
        _loadingFallback = false;
        SetReadOnly(note.IsTrashed);
        if (_host.IsReady) PushContent();
    }

    private bool _loadingFallback;

    private void SetReadOnly(bool readOnly)
    {
        _readOnly = readOnly;
        _fallback.IsReadOnly = readOnly;
        _toolbar.IsEnabled = !readOnly;
        if (_host.IsReady)
            _ = _host.ExecuteScriptAsync(
                "document.getElementById('editor').contentEditable = " + (readOnly ? "false" : "true") + ";");
    }

    private void OnReady()
    {
        _status.Text = "富文本编辑器已就绪";
        PushContent();
    }

    private void OnFailed(string message)
    {
        _webFailed = true;
        _host.IsVisible = false;
        _fallback.IsVisible = true;
        _status.Text = "WebView2 不可用，已退回纯文本：" + message;
        StatusChanged?.Invoke("WebView2 运行时不可用，富文本暂以纯文本编辑（不会破坏已有 HTML）。");
    }

    private void PushContent()
    {
        if (_pendingHtml == null || !_host.IsReady || _webFailed)
            return;
        var script = "window.tn && window.tn.setContent(" + JsonSerializer.Serialize(_pendingHtml) + ");"
                     + "document.getElementById('editor').contentEditable = "
                     + (_readOnly ? "false" : "true") + ";";
        _ = _host.ExecuteScriptAsync(script);
        _status.Text = "已保存 · 受控 HTML";
    }

    private void OnWebMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "ready")
            {
                PushContent();
                return;
            }
            if (type != "changed" || !root.TryGetProperty("html", out var htmlElement))
                return;
            var html = htmlElement.GetString() ?? "";
            if (_readOnly)
                return;
            _lastHtml = html;

            _saveDelay?.Cancel();
            _saveDelay?.Dispose();
            _saveDelay = new CancellationTokenSource();
            var token = _saveDelay.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() => SaveHtml(html));
                }
                catch (OperationCanceledException) { }
            }, token);
        }
        catch (JsonException)
        {
            // 非 bridge 消息忽略
        }
    }

    private void SaveHtml(string html)
    {
        if (_readOnly || _currentId == Guid.Empty)
            return;
        var note = _session.Notes.Find(_currentId);
        if (note == null || note.IsTrashed)
            return;

        var parsed = RichTextHtmlCodec.Decode(html);
        var onlyEmpty = parsed.PlainText.Trim().Length == 0
                        && parsed.Blocks.All(block => block.Runs.All(run => run.Image == null));
        var normalized = onlyEmpty ? "" : RichTextHtmlCodec.Encode(parsed);
        if (note.ContentFormat == NoteContentFormat.richText && note.Body == normalized)
            return;

        _currentBody = normalized;
        _currentFormat = NoteContentFormat.richText;
        _session.Notes.UpdateContent(_currentId, normalized, NoteContentFormat.richText);
        _status.Text = "已保存 · " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void SaveFallback()
    {
        if (_loadingFallback || !_webFailed || _readOnly || _currentId == Guid.Empty)
            return;
        var note = _session.Notes.Find(_currentId);
        if (note == null || note.ContentFormat != NoteContentFormat.richText)
            return;
        // 退回模式只改可见文字，保留原 HTML 结构不被破坏
        _status.Text = "纯文本模式（原 HTML 未改动）";
    }

    private void Run(string method, params string[] arguments)
    {
        if (_readOnly || !_host.IsReady || _webFailed)
            return;
        var args = string.Join(",", arguments.Select(value => JsonSerializer.Serialize(value)));
        _ = _host.ExecuteScriptAsync("window.tn && window.tn." + method + "(" + args + ");");
    }

    private async void InsertImage()
    {
        if (_readOnly || _currentId == Guid.Empty)
            return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
            return;
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择一张图片",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            var file = files.FirstOrDefault();
            if (file == null)
                return;
            var source = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(source))
                return;
            var relative = _session.Notes.Attachments.ImportImage(source, _currentId);
            if (!_host.IsReady)
            {
                StatusChanged?.Invoke("图片已导入附件目录：" + relative);
                return;
            }
            Run("insertImage", relative);
            StatusChanged?.Invoke("已插入图片，保存后会随笔记一起同步到网盘。");
        }
        catch (Exception error)
        {
            StatusChanged?.Invoke("插入图片失败：" + error.Message);
        }
    }

    /// <summary>切换笔记 / 关窗前把最后一次收到的内容立刻落盘（不阻塞 UI 线程）。</summary>
    public void Flush()
    {
        if (_lastHtml == null || _readOnly)
            return;
        var html = _lastHtml;
        _lastHtml = null;
        _saveDelay?.Cancel();
        SaveHtml(html);
    }
}
