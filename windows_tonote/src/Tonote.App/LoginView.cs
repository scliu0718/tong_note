using Avalonia;
using Tonote.Core;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tonote.App;

public sealed class LoginView : UserControl
{
    public LoginView(BaiduLogin login, Action enterLocal)
    {
        var banner = new TextBlock
        {
            Foreground = Palette.Red,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 340,
            IsVisible = false,
        };
        login.Notice += (text, error) =>
        {
            banner.Text = text;
            banner.Foreground = error ? Palette.Red : Palette.TextSecondary;
            banner.IsVisible = true;
        };

        var form = Login(banner, () => login.Start("popup"), () => login.Start("page"), enterLocal);
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Background = Palette.WindowBg,
        };
        layout.Children.Add(Brand());
        layout.Children.Add(form);
        Grid.SetColumn(form, 1);
        Content = layout;
    }

    private static Control Brand()
    {
        var canvas = new Canvas { Background = Brushes.Transparent };
        var glowA = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 420,
            Height = 420,
            Fill = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
        };
        var glowB = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 380,
            Height = 380,
            Fill = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)),
        };
        Canvas.SetLeft(glowA, 220);
        Canvas.SetTop(glowA, -180);
        Canvas.SetLeft(glowB, 260);
        Canvas.SetTop(glowB, 360);
        canvas.Children.Add(glowA);
        canvas.Children.Add(glowB);

        var copy = new StackPanel
        {
            Margin = new Thickness(48),
            Spacing = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "你的每一份记录，\n都交给你自己保管。",
                    Foreground = Palette.White,
                    FontSize = 32,
                    FontWeight = FontWeight.Bold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "不用订阅、不锁数据。笔记直接写入你自己的百度网盘，随时可导出、可迁移。",
                    Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400,
                },
                Feature("数据存储于百度网盘，跨 Mac / Windows / iOS / Android 同步"),
                Feature("笔记、表格、任务和附件，格式开放可迁移"),
                Feature("离线编辑，联网自动增量同步"),
            },
        };

        return new Panel
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#17C26B"), 0),
                    new GradientStop(Color.Parse("#0F9D52"), 0.55),
                    new GradientStop(Color.Parse("#0C7A40"), 1),
                },
            },
            Children = { canvas, copy },
        };
    }

    private static Control Feature(string text)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)),
                    Child = new TextBlock
                    {
                        Text = "✓",
                        Foreground = Palette.White,
                        FontSize = 11,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
                new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
    }

    private static Control Login(TextBlock banner, Action accountLogin, Action scanLogin, Action enterLocal)
    {
        var panel = new StackPanel
        {
            Spacing = 16,
            MaxWidth = 380,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(36),
            Children =
            {
                new TextBlock
                {
                    Text = "欢迎使用同笔记",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Foreground = Palette.TextPrimary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "使用百度网盘账号登录，笔记将存储于你的网盘",
                    FontSize = 13,
                    Foreground = Palette.TextTertiary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                },
                new TextBlock
                {
                    Text = BaiduPanConfig.IsConfigured
                        ? "已读取 baidupan.secret.json"
                        : "还没有读到 baidupan.secret.json",
                    FontSize = 12,
                    Foreground = BaiduPanConfig.IsConfigured ? Palette.Accent : Palette.Red,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                PrimaryButton("使用百度网盘账号登录", accountLogin),
                banner,
                OrDivider(),
                SecondaryButton("浏览器扫码登录", scanLogin),
                new TextBlock
                {
                    Text = "登录即代表你授权同笔记通过百度网盘开放平台读写 /apps/tong_log 目录。授权信息只保存在本机。",
                    FontSize = 11,
                    Foreground = Palette.TextQuaternary,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                },
                SecondaryButton("先使用本机笔记", enterLocal, Palette.Accent),
            },
        };
        return panel;
    }

    private static Button PrimaryButton(string text, Action click)
    {
        var button = new Button
        {
            Content = text,
            Background = Palette.Baidu,
            Foreground = Palette.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Height = 48,
            FontSize = 15,
            CornerRadius = new CornerRadius(10),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static Button SecondaryButton(string text, Action click, IBrush? foreground = null)
    {
        var button = new Button
        {
            Content = text,
            Background = Palette.Panel,
            Foreground = foreground ?? Palette.Baidu,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Height = 40,
            FontSize = 13,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Palette.Baidu,
            BorderThickness = new Thickness(1),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static Control OrDivider()
    {
        var left = new Border { Height = 1, Background = Palette.Line, VerticalAlignment = VerticalAlignment.Center };
        var right = new Border { Height = 1, Background = Palette.Line, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock
        {
            Text = "或",
            Margin = new Thickness(12, 0),
            Foreground = Palette.TextQuaternary,
            FontSize = 12,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            Children = { left, text, right },
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(right, 2);
        return grid;
    }
}
