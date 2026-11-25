using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tonote.Core;
using System.Threading;

namespace Tonote.App;

/// <summary>
/// Mac 端 .alert / .sheet / .popover 的 Windows 等价实现。
/// 统一使用无系统标题栏的白色圆角窗口 + 0.75pt 分割线 + 居中按钮，
/// 文案与「同笔记-Mac端UI设计.html」:987-1040 的交互态清单一致。
/// </summary>
public static class Dialogs
{
    /// <summary>W-35：同一时刻只允许一个确认 / 失败对话框，避免连续触发叠窗。</summary>
    private static readonly SemaphoreSlim DialogGate = new(1, 1);

    private static Window? Owner(Control anchor) => TopLevel.GetTopLevel(anchor) as Window;

    /// <summary>
    /// W-19「绝不静默失败」统一入口：可见对话框 + 脱敏诊断日志（不含 token / 正文）。
    /// </summary>
    public static async Task ReportFailure(Control anchor, string title, string reason, string category = "app")
    {
        DiagnosticLog.Write(category, title + " | " + reason);
        var owner = Owner(anchor);
        if (owner == null) return;
        await DialogGate.WaitAsync();
        try
        {
            await new NoticeDialog(title, reason).ShowDialog<bool>(owner);
        }
        finally
        {
            DialogGate.Release();
        }
    }

    public static async Task<bool> Confirm(Control anchor, string title, string message,
        string confirmText = "删除", string cancelText = "取消", bool danger = true)
    {
        var owner = Owner(anchor);
        if (owner == null) return false;
        await DialogGate.WaitAsync();
        try
        {
            var dialog = new ConfirmDialog(title, message, confirmText, cancelText, danger);
            return await dialog.ShowDialog<bool>(owner);
        }
        finally
        {
            DialogGate.Release();
        }
    }

    public static async Task<string?> Prompt(Control anchor, string title, string message,
        string watermark = "", string initial = "")
    {
        var owner = Owner(anchor);
        if (owner == null) return null;
        return await new PromptDialog(title, message, watermark, initial).ShowDialog<string?>(owner);
    }

    public static async Task<DateTime?> PickDate(Control anchor, DateTime? current)
    {
        var owner = Owner(anchor);
        if (owner == null) return null;
        return await new CalendarDialog(current ?? DateTime.Today).ShowDialog<DateTime?>(owner);
    }

    public static async Task ShowSettings(Control anchor, AppSession session, Action onLogout)
    {
        var owner = Owner(anchor);
        if (owner == null) return;
        await new SettingsSheet(session, onLogout).ShowDialog(owner);
    }

    /// <summary>244 宽头像 popover（Mac SidebarView.swift:199-261）。</summary>
    public static async Task PickAvatar(Control anchor, AppSession session)
    {
        var owner = Owner(anchor);
        if (owner == null) return;
        await new AvatarPicker(session).ShowDialog(owner);
    }

    private static GuideWindow? _guide;

    public static void ShowGuide(Control anchor)
    {
        // W-29：单例，重复点击不叠窗；登录页也能进入
        if (_guide != null) { _guide.Activate(); return; }
        var window = new GuideWindow();
        _guide = window;
        window.Closed += (_, _) => _guide = null;
        var owner = Owner(anchor);
        if (owner == null) window.Show();
        else window.Show(owner);
    }

    private static Border Configure(Window window, string title, double width)
    {
        window.Title = title;
        window.Width = width;
        window.SizeToContent = SizeToContent.Height;
        window.CanResize = false;
        window.ShowInTaskbar = false;
        window.SystemDecorations = SystemDecorations.None;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Background = Palette.Panel;
        return new Border
        {
            Background = Palette.Panel,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Metrics.RadiusSheet),
            BoxShadow = Palette.ShadowMd,
        };
    }

    private static StackPanel DialogBody(string title, string message, out Border line)
    {
        line = new Border { Height = 0.75, Background = Palette.Line, Margin = new Thickness(0, 12, 0, 14) };
        return new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 18),
            Children =
            {
                Ui.Title(title, 17),
                line,
                new TextBlock
                {
                    Text = message,
                    FontSize = 13,
                    Foreground = Palette.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                },
            },
        };
    }

    private static StackPanel ButtonRow(params Control[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12,
            // 正文最后一行与按钮区之间的垂直间距 24px
            Margin = new Thickness(0, 24, 0, 0),
        };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    private sealed class ConfirmDialog : Window
    {
        public ConfirmDialog(string title, string message, string confirmText, string cancelText, bool danger)
        {
            var frame = Configure(this, title, 360);
            var body = DialogBody(title, message, out _);
            var ok = danger
                ? Ui.DangerFill(confirmText, () => Close(true))
                : Ui.Primary(confirmText, () => Close(true));
            ok.MinWidth = 96;
            var cancel = Ui.Ghost(cancelText, () => Close(false));
            cancel.MinWidth = 96;
            body.Children.Add(ButtonRow(cancel, ok));
            frame.Child = body;
            Content = frame;
        }
    }

    /// <summary>单按钮「操作失败」窗口（W-19）。</summary>
    private sealed class NoticeDialog : Window
    {
        public NoticeDialog(string title, string message)
        {
            var frame = Configure(this, title, 380);
            var body = DialogBody(title, message, out _);
            var ok = Ui.DangerFill("知道了", () => Close(true));
            ok.MinWidth = 96;
            body.Children.Add(ButtonRow(ok));
            frame.Child = body;
            Content = frame;
        }
    }

    private sealed class PromptDialog : Window
    {
        private readonly TextBox _input;

        public PromptDialog(string title, string message, string watermark, string initial)
        {
            var frame = Configure(this, title, 380);
            var body = DialogBody(title, message, out _);
            _input = new TextBox
            {
                Text = initial,
                Watermark = watermark,
                FontSize = 13,
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(Metrics.RadiusButton),
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0),
            };
            _input.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) Close(_input.Text ?? "");
            };
            // 全局 TextBox 样式把边框压成 0，这里用外层 Border 画出可见输入框，
            // 否则「修改笔记本」弹窗里看不出文字区域可以编辑。
            var inputFrame = new Border
            {
                Background = Palette.Panel,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Metrics.RadiusButton),
                Margin = new Thickness(0, 0, 0, 16),
                Child = _input,
            };
            body.Children.Add(inputFrame);
            var ok = Ui.Primary("确定", () => Close(_input.Text ?? ""));
            ok.MinWidth = 96;
            var cancel = Ui.Ghost("取消", () => Close(null));
            cancel.MinWidth = 96;
            body.Children.Add(ButtonRow(cancel, ok));
            frame.Child = body;
            Content = frame;
        }
    }

    /// <summary>348 宽自定义日历：月份导航 / 今天 / 已选 / 6×7 网格（Mac TodoView.swift:887-1066）。</summary>
    private sealed class CalendarDialog : Window
    {
        private DateTime _month;
        private readonly DateTime _selected;
        private readonly TextBlock _caption = new() { FontSize = 13, FontWeight = FontWeight.SemiBold };
        private readonly Grid _days = new() { Margin = new Thickness(0, 8) };

        public CalendarDialog(DateTime selected)
        {
            _selected = selected.Date;
            _month = new DateTime(_selected.Year, _selected.Month, 1);

            Title = "选择日期";
            Width = Metrics.PopCalendar;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            SystemDecorations = SystemDecorations.None;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Palette.Panel;

            for (var i = 0; i < 7; i++) _days.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var i = 0; i < 7; i++) _days.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var nav = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            var prev = Ui.Tool(Glyphs.ChevronLeft, "上个月", () => { _month = _month.AddMonths(-1); Render(); });
            var next = Ui.Tool(Glyphs.ChevronRight, "下个月", () => { _month = _month.AddMonths(1); Render(); });
            var today = Ui.Small("今天", () => Close(DateTime.Today));
            Grid.SetColumn(prev, 0);
            Grid.SetColumn(_caption, 1);
            _caption.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(today, 2);
            Grid.SetColumn(next, 3);
            nav.Children.Add(prev); nav.Children.Add(_caption); nav.Children.Add(today); nav.Children.Add(next);

            var selectedLabel = Ui.Text("已选 " + _selected.ToString("yyyy年M月d日"),
                11, null, Palette.TextTertiary);
            selectedLabel.HorizontalAlignment = HorizontalAlignment.Center;
            var stack = new StackPanel { Margin = new Thickness(12), Children = { nav, selectedLabel, _days, Ui.Divider(marginV: 6) } };
            var clear = Ui.Ghost("清除日期", () => Close(null));
            clear.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(clear);

            Content = new Border
            {
                Background = Palette.Panel,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                BoxShadow = Palette.ShadowMd,
                Child = stack,
            };
            Render();
        }

        private void Render()
        {
            _caption.Text = _month.ToString("yyyy 年 M 月");
            _days.Children.Clear();
            _days.RowDefinitions.Clear();
            for (var i = 0; i < 7; i++) _days.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var week = new[] { "日", "一", "二", "三", "四", "五", "六" };
            for (var i = 0; i < week.Length; i++)
            {
                var label = Ui.Text(week[i], 11, FontWeight.SemiBold, Palette.TextTertiary);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(label, i);
                _days.Children.Add(label);
            }

            var first = new DateTime(_month.Year, _month.Month, 1);
            var offset = (int)first.DayOfWeek;
            var cursor = first.AddDays(-offset);
            for (var i = 0; i < 42; i++, cursor = cursor.AddDays(1))
            {
                var day = cursor;
                var inMonth = day.Month == _month.Month;
                var isSelected = day.Date == _selected;
                var isToday = day.Date == DateTime.Today;
                var button = new Button
                {
                    Content = day.Day.ToString(),
                    Height = 30,
                    Margin = new Thickness(1.5),
                    FontSize = 12,
                    CornerRadius = new CornerRadius(7),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Background = isSelected ? Palette.Accent : Brushes.Transparent,
                    Foreground = isSelected ? Palette.White : inMonth ? Palette.TextPrimary : Palette.TextQuaternary,
                    BorderBrush = isToday && !isSelected ? Palette.AccentTint2 : Brushes.Transparent,
                    BorderThickness = new Thickness(isToday && !isSelected ? 2 : 0),
                };
                button.Click += (_, _) => Close(day.Date);
                Grid.SetRow(button, 1 + i / 7);
                Grid.SetColumn(button, i % 7);
                _days.Children.Add(button);
            }
        }
    }

    /// <summary>244 宽头像选择器：8 个预设渐变 + 当前选中态。</summary>
    private sealed class AvatarPicker : Window
    {
        private static string CloudStatus(AppSession session) => session.Avatar.CloudState switch
        {
            "uploading" => "正在上传…",
            "synced" => "已同步",
            "failed" => "上传失败（已保存到本地）",
            _ => "更换后自动上传",
        };

        private static async Task RefreshCloudStatus(AppSession session, TextBlock label)
        {
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(500);
                if (session.Avatar.CloudState is "synced" or "failed") break;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => label.Text = CloudStatus(session));
        }

        public AvatarPicker(AppSession session)
        {
            var frame = Configure(this, "选择头像", Metrics.PopAvatar);
            var stack = new StackPanel { Margin = new Thickness(16, 14), Spacing = 10 };
            stack.Children.Add(Ui.Text("选择头像", 13, FontWeight.SemiBold));
            // W-33：244 宽严格 4 列网格，选中打勾而非蓝框
            var grid = new Grid();
            for (var c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var status = Ui.Text(CloudStatus(session), 11, null, Palette.TextTertiary);
            status.TextWrapping = TextWrapping.Wrap;
            for (var i = 0; i < AvatarStore.Presets.Count; i++)
            {
                var choice = AvatarStore.Presets[i];
                var cell = new Grid { Width = 44, Height = 44, Margin = new Thickness(2, 3) };
                var selected = session.Avatar.Current.Id == choice.Id;
                cell.Children.Add(Ui.AvatarBadge(choice, 42));
                if (selected)
                    cell.Children.Add(new Border
                    {
                        Width = 18, Height = 18,
                        CornerRadius = new CornerRadius(9),
                        Background = Palette.Accent,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Child = Ui.Icon(Glyphs.Check, 10, Palette.White),
                    });
                var hit = new Button
                {
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                hit.Click += (_, _) =>
                {
                    session.Avatar.Select(choice.Id);
                    status.Text = CloudStatus(session);
                    _ = RefreshCloudStatus(session, status);
                };
                cell.Children.Add(hit);
                Grid.SetRow(cell, i / 4);
                Grid.SetColumn(cell, i % 4);
                while (grid.RowDefinitions.Count <= i / 4) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.Children.Add(cell);
            }
            stack.Children.Add(grid);
            stack.Children.Add(status);
            var close = Ui.Ghost("取消", Close);
            close.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(close);
            frame.Child = stack;
            Content = frame;
            _ = RefreshCloudStatus(session, status);
        }
    }

    /// <summary>900×640 「同步与存储」sheet（Mac SyncView.swift）。</summary>
    private sealed class SettingsSheet : Window
    {
        private readonly AppSession _session;
        private readonly Action _onLogout;
        private readonly TextBlock _account = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary };
        private readonly TextBlock _sync = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary };
        private readonly Border _syncBadge = new()
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        private readonly TextBlock _syncBadgeText = new() { FontSize = 11.5, FontWeight = FontWeight.SemiBold };
        private readonly StackPanel _devices = new() { Spacing = 4 };
        private readonly TextBlock _quota = new() { FontSize = 24, FontWeight = FontWeight.Bold };
        private readonly ProgressBar _usage = new() { Height = 6, Minimum = 0, Maximum = 100 };

        public SettingsSheet(AppSession session, Action onLogout)
        {
            _session = session;
            _onLogout = onLogout;
            Title = "同步与存储";
            Width = Metrics.SheetWidth;
            Height = Metrics.SheetHeight;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Palette.WindowBg;

            var close = Ui.Ghost("关闭（Esc）", Close);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(26, 22, 26, 12),
                Children = { Ui.Title("同步与存储", Metrics.FontHeader), close },
            };
            Grid.SetColumn(close, 1);

            var left = new StackPanel { Spacing = 14, Children = { SyncCard(), DirectoryCard(), AccountCard(), DeviceCard() } };
            var right = new StackPanel { Spacing = 14, Children = { QuotaCard(), DataCard(), GuideCard(), LocalCard() } };

            var body = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,280"),
                Margin = new Thickness(26, 0, 26, 22),
                Children =
                {
                    new ScrollViewer { Content = left, Margin = new Thickness(0, 0, 18, 0) },
                    new Border
                    {
                        Background = Palette.SidebarBg,
                        Padding = new Thickness(16),
                        Child = new ScrollViewer { Content = right },
                    },
                },
            };
            Grid.SetColumn((Control)body.Children[1], 1);

            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { header, body } };
            Grid.SetRow(body, 1);
            Content = root;

            KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
            _ = LoadAsync();
        }

        private Button? _syncNow;

        private Control SyncCard()
        {
            _syncNow = Ui.Primary("立即同步", () => _ = _session.Sync.QuickSyncAsync());
            RefreshSyncText();
            _syncBadge.Child = _syncBadgeText;
            return Ui.Card(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Ui.Text("同步状态", Metrics.FontGroup, FontWeight.SemiBold),
                    _syncBadge,
                    _sync,
                    Ui.Text("同步范围：笔记 · 任务", 11, null, Palette.TextTertiary),
                    Ui.Text("按篇存在 /apps/tong_log/notes/", 11, null, Palette.TextQuaternary),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _syncNow } },
                },
            });
        }

        private void RefreshSyncText()
        {
            var status = _session.Sync.Status;
            var last = _session.Sync.LastSyncDate;
            var text = status.Kind switch
            {
                SyncStatusKind.Syncing => "同步中 · 正在拉取云端并对比…",
                SyncStatusKind.Success => "已同步" + (last == null ? "" : " · 上次 " + last.Value.ToString("MM-dd HH:mm")),
                SyncStatusKind.Failed => "同步失败 · " + status.Message,
                _ => "等待同步",
            };
            _sync.Text = text + (string.IsNullOrWhiteSpace(status.Message) || status.Kind == SyncStatusKind.Failed
                ? "" : " · " + status.Message);
            _sync.Foreground = status.Kind == SyncStatusKind.Failed ? Palette.Red : Palette.TextSecondary;
            // 四态徽标：idle 灰 / run 琥珀 / ok 绿 / err 红（设计稿 :375-378）
            var (label, background, foreground) = status.Kind switch
            {
                SyncStatusKind.Syncing => ("同步中", Palette.SyncRunBg, Palette.SyncRunText),
                SyncStatusKind.Success => ("已同步", Palette.AccentTint, Palette.AccentHover),
                SyncStatusKind.Failed => ("同步失败", Palette.RedTint, Palette.Red),
                _ => ("待同步", Palette.Pill, Palette.TextTertiary),
            };
            _syncBadgeText.Text = "● " + label;
            _syncBadgeText.Foreground = foreground;
            _syncBadge.Background = background;
            if (_syncNow != null)
                _syncNow.Content = status.Kind switch
                {
                    SyncStatusKind.Syncing => "同步中…",
                    SyncStatusKind.Success => "再次同步",
                    SyncStatusKind.Failed => "重试同步",
                    _ => "同步全部",
                };
        }

        private static Control DirectoryCard()
        {
            var tree = new TextBlock
            {
                FontFamily = FontFamily.Parse("Consolas, Courier New"),
                FontSize = 11.5,
                Foreground = Palette.TextSecondary,
                Text = "/apps/tong_log/\n ├─ notes/<UUID>.json\n ├─ todos/<UUID>.json\n ├─ attachments/<UUID>/…\n ├─ notebooks.json\n ├─ devices/<device_id>.json\n ├─ avatar.json\n └─ manifest.json（旧版迁移）",
            };
            return Ui.Card(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Ui.Text("云端目录结构", Metrics.FontGroup, FontWeight.SemiBold),
                    Ui.Text("笔记数据存放于你自己的百度网盘，同笔记仅做读写，不另建服务器。", 11, null, Palette.TextTertiary),
                    tree,
                },
            });
        }

        private Control AccountCard()
        {
            var avatars = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var preset in AvatarStore.Presets)
            {
                var choice = preset;
                var button = new Button
                {
                    Content = Ui.AvatarBadge(choice, 32),
                    Width = 32, Height = 32,
                    Margin = new Thickness(0, 0, 6, 6),
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                };
                button.Click += (_, _) => _session.Avatar.Select(choice.Id);
                avatars.Children.Add(button);
            }
            var reauthorize = Ui.Small("重新授权", () =>
            {
                _session.Logout();
                _onLogout();
                Close();
            }, null, 11);
            return Ui.Card(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Ui.Text("账号与安全", Metrics.FontGroup, FontWeight.SemiBold),
                    _account,
                    Ui.Text("授权方式：百度网盘开放平台 OAuth 2.0", 11, null, Palette.TextTertiary),
                    reauthorize,
                    Ui.Text("头像（8 个预设）", 11, null, Palette.TextTertiary), avatars,
                },
            });
        }

        private Control DeviceCard()
        {
            var refresh = Ui.Small("刷新设备列表", () => _ = LoadAsync());
            return Ui.Card(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Ui.Text("登录设备", Metrics.FontGroup, FontWeight.SemiBold),
                    Ui.Text("已授权的设备可随时下线；本机被下线后需重新登录。", 11, null, Palette.TextTertiary),
                    _devices, refresh,
                },
            });
        }

        private Control QuotaCard()
        {
            _quota.Text = "— / —";
            return Ui.Card(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Ui.Text("存储空间", 13, FontWeight.SemiBold),
                    _quota,
                    _usage,
                    Ui.Text("已用空间 · 以百度网盘为准", 11, null, Palette.TextTertiary),
                    Ui.Text("数据完全归你所有，可随时导出迁移。", 11, null, Palette.TextQuaternary),
                },
            }, background: Palette.Panel);
        }

        private static Control DataCard() => Ui.Card(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Ui.Text("数据说明", 13, FontWeight.SemiBold),
                new TextBlock
                {
                    FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary,
                    Text = "所有笔记为开放格式，数据完全归你所有，可随时导出迁移。正文是三端共用的受控 HTML，删除使用 purged 墓碑。",
                },
                Ui.Text("同笔记 · 数据属于你自己 / 无需订阅 · 无服务器存储", 10.5, null, Palette.TextQuaternary),
            },
        });

        /// <summary>PC-SE-07：本机数据目录、版本号与一键复制诊断信息（严格不含凭证 / 正文）。</summary>
        private Control LocalCard()
        {
            var version = typeof(AppSession).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            var open = Ui.Ghost("在资源管理器中打开", () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _session.Root)
                    {
                        UseShellExecute = true,
                    });
                }
                catch (Exception error) { DiagnosticLog.Write("app", "打开数据目录失败：" + error.Message); }
            }, 11);
            var copy = Ui.Ghost("复制诊断信息", () =>
            {
                var status = _session.Sync.Status;
                var text = string.Join("\n",
                    "同笔记 " + version,
                    "数据目录：" + _session.Root,
                    "笔记：" + _session.Notes.ActiveNotes.Count + " 篇 · 任务：" + _session.Todos.Todos.Count(todo => !todo.Purged) + " 条",
                    "同步状态：" + status.Kind + " · 上次同步：" + (_session.Sync.LastSyncDate?.ToString("yyyy-MM-dd HH:mm") ?? "无"),
                    "最近错误：" + (status.Kind == SyncStatusKind.Failed ? status.Message : "无"));
                _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
                DiagnosticLog.Write("app", "已复制诊断信息（已脱敏）");
            }, 11);
            return Ui.Card(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Ui.Text("本机数据", 13, FontWeight.SemiBold),
                    new TextBlock { Text = _session.Root, FontSize = 11, Foreground = Palette.TextSecondary, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "版本 " + version, FontSize = 11, Foreground = Palette.TextTertiary },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, copy } },
                },
            });
        }

        private Control GuideCard()
        {
            var open = Ui.Ghost("查看使用说明", () => Dialogs.ShowGuide(this));
            var logout = Ui.Danger("退出登录", () =>
            {
                Close();
                _onLogout();
            });
            return Ui.Card(new StackPanel
            {
                Spacing = 8,
                Children = { Ui.Text("使用说明", 13, FontWeight.SemiBold), open, logout },
            });
        }

        private async Task LoadAsync()
        {
            RefreshSyncText();
            if (!_session.IsSignedIn)
            {
                _account.Text = "未登录百度网盘。登录后笔记、任务、笔记本会按篇同步。";
                _devices.Children.Clear();
                _devices.Children.Add(Ui.Text("登录后显示设备列表。", 11, null, Palette.TextTertiary));
                return;
            }
            try
            {
                var token = await _session.Pan.ValidTokenAsync().ConfigureAwait(false);
                var info = await _session.Pan.UserInfoAsync(token.AccessToken).ConfigureAwait(false);
                var quota = await _session.Pan.QuotaAsync(token.AccessToken).ConfigureAwait(false);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _account.Text = info.DisplayName + " · 已授权";
                    _quota.Text = quota.UsedText + " / " + quota.TotalText;
                    _usage.Value = Math.Min(100, quota.UsedRatio * 100);
                });
            }
            catch
            {
                // W-25：账号信息读取失败静默——显示「已授权」与 — / —，不出现错误文本
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _account.Text = "已授权";
                    _quota.Text = "— / —";
                    _usage.Value = 0;
                });
            }

            try
            {
                var records = (await _session.Devices.ListDevicesAsync().ConfigureAwait(false)).ToList();
                var mine = _session.Devices.CurrentDeviceId;
                // W-36：云端列表为空或漏掉本机时，用 LocalSnapshot 补一行，避免误显示「暂无设备记录」
                if (!records.Any(record => record.DeviceId == mine))
                    records.Add(_session.Devices.LocalSnapshot());
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _devices.Children.Clear();
                    if (records.Count == 0)
                    {
                        _devices.Children.Add(Ui.Text("暂无设备记录", 11, null, Palette.TextTertiary));
                        return;
                    }
                    foreach (var record in records)
                    {
                        var target = record.DeviceId;
                        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                        row.Children.Add(Ui.Text(record.DeviceName + (target == mine ? "（本机）" : "") + (record.Kicked ? " · 已下线" : ""), 11.5,
                            null, record.Kicked ? Palette.TextTertiary : Palette.TextPrimary));
                        var kick = Ui.Small("下线", async () =>
                        {
                            var yes = await Confirm(this, "让这台设备下线？", "「" + record.DeviceName + "」将在本轮同步后被强制退出登录。");
                            if (!yes) return;
                            try { await _session.Devices.KickAsync(target); await LoadAsync(); }
                            catch (Exception error) { _devices.Children.Add(Ui.Text("下线失败：" + error.Message, 11, null, Palette.Red)); }
                        });
                        kick.IsVisible = target != mine && !record.Kicked;
                        Grid.SetColumn(kick, 1);
                        row.Children.Add(kick);
                        _devices.Children.Add(row);
                    }
                });
            }
            catch (Exception error)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _devices.Children.Clear();
                    _devices.Children.Add(Ui.Text("读取设备列表失败：" + error.Message, 11, null, Palette.Red));
                });
            }
        }
    }

    /// <summary>860×740 使用说明窗口（Mac UserGuideView.swift）。</summary>
    private sealed class GuideWindow : Window
    {
        public GuideWindow()
        {
            Title = "使用说明";
            Width = Metrics.GuideWidth;
            Height = Metrics.GuideHeight;
            MinWidth = 560;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Palette.Panel;

            // W-29：随包 guide/index.html 优先；缺失时给出明确文案而不是空白窗
            var guidePath = Path.Combine(AppContext.BaseDirectory, "guide", "index.html");
            if (File.Exists(guidePath))
            {
                var host = new WebView2Host(Path.Combine(AppPaths.DefaultRoot(), "webview2-guide"))
                {
                    Margin = new Thickness(0),
                };
                host.Navigate(new Uri(guidePath).AbsoluteUri);
                Content = host;
                return;
            }

            var sections = new (string Title, string[] Lines)[]
            {
                ("开始使用", ["登录后笔记、任务、笔记本会按篇同步到百度网盘 /apps/tong_log/。", "左侧可切换笔记本与标签，中间是笔记列表，右侧是编辑器。"]),
                ("笔记与富文本", ["标题直接点顶部输入框修改；正文支持正文/标题 1-4、加粗、斜体、下划线、删除线、字号、文字色、背景色、引用、代码块、列表、链接、图片与表格。", "插入表格后可在工具条里增删行列、设置单元格对齐与底色。"]),
                ("快任务", ["四组：待完成 / 进行中 / 已完成 / 已终止。点击状态圈或右侧按钮流转。", "展开任务可写备注（支持图片）、设置计划日期；逾期任务会显示红框与角标。", "已完成与已终止的任务内容只读，但仍可流转状态或删除。"]),
                ("同步", ["本地改动会自动上传；也可在设置或任务页点「同步」。", "「快同步」是独立笔记，点「立即同步本篇」只推这一篇。", "冲突时保留双方，生成「（冲突副本）」笔记。"]),
                ("数据", ["笔记、任务、笔记本、附件三端共用同一套 JSON 与受控 HTML。", "删除使用 purged 墓碑，不会立刻抹掉云端文件。"]),
            };

            var stack = new StackPanel { Margin = new Thickness(32, 28), Spacing = 20 };
            stack.Children.Add(Ui.Title("同笔记 · 使用说明", 24));
            stack.Children.Add(Ui.Text("记录 · 同步 · 属于你", 13, null, Palette.TextTertiary));
            stack.Children.Add(Ui.Text("没有找到使用说明。请完全退出后重新打开同笔记再试。",
                12, null, Palette.TextQuaternary));
            foreach (var (title, lines) in sections)
            {
                var card = new StackPanel { Spacing = 6 };
                card.Children.Add(Ui.Text(title, 15, FontWeight.SemiBold));
                foreach (var line in lines)
                    card.Children.Add(new TextBlock { Text = "· " + line, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary, LineHeight = 20 });
                stack.Children.Add(Ui.Card(card));
            }
            var close = Ui.Primary("关闭", Close);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            stack.Children.Add(close);
            Content = new ScrollViewer { Content = stack };
        }
    }
}
