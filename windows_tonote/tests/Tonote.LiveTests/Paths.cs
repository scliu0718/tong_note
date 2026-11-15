namespace Tonote.LiveTests;

/// <summary>
/// 测试套件用到的本机路径。全部从仓库根目录推导，避免把某台机器的绝对路径写进源码；
/// 数据目录可用 TONOTE_TEST_ROOT 覆盖。
/// </summary>
public static class Paths
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string WindowsRoot => Path.Combine(RepoRoot, "windows_tonote");
    public static string AppDir => Path.Combine(WindowsRoot, "src", "Tonote.App");
    public static string OutputDir => Path.Combine(AppDir, "bin", "Debug", "net8.0");
    public static string Exe => Path.Combine(OutputDir, "Tonote.exe");
    public static string Secret => Path.Combine(WindowsRoot, "baidupan.secret.json");

    /// <summary>真实登录态。只读取、不修改；测试会复制到隔离目录。</summary>
    public static string SourceToken => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tonote", "baidupan-token.json");

    public static string TestDataRoot =>
        Environment.GetEnvironmentVariable("TONOTE_TEST_ROOT")
        ?? Path.Combine(WindowsRoot, ".test-data");

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "同笔记-Mac端UI设计.html")))
                    return dir.FullName;
            }
        }
        throw new InvalidOperationException("找不到仓库根目录（同笔记-Mac端UI设计.html）");
    }
}
