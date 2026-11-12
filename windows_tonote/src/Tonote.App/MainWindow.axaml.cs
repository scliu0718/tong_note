using Avalonia.Controls;
using Avalonia.Media;

namespace Tonote.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FontFamily = FontFamily.Parse("Microsoft YaHei UI, PingFang SC, Segoe UI, Inter");
        var session = new AppSession();
        Root.Content = new LoginView(() => Root.Content = new ShellView(session));
    }
}
