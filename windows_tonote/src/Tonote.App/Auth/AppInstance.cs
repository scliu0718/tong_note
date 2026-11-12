using System.IO.Pipes;
using System.Text;

namespace Tonote.App;

/// <summary>
/// 浏览器回调会再启动一个进程。已有窗口时把 tongnote:// 交回去，新进程退出。
/// </summary>
internal static class AppInstance
{
    private const string MutexName = "TonoteOAuthSingleInstance";
    private const string PipeName = "tonote-oauth-callback";

    private static Mutex? _mutex;

    public static string? StartupCallback { get; private set; }

    public static event Action<string>? Callback;

    public static bool HandOffAndExit(string[] args)
    {
        var callback = FindCallback(args);
        _mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            if (callback != null)
                Send(callback);
            return true;
        }

        StartupCallback = callback;
        Listen();
        ProtocolRegistration.Ensure();
        return false;
    }

    public static string? FindCallback(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            var text = arg.Trim().Trim('"');
            if (text.StartsWith("tongnote://", StringComparison.OrdinalIgnoreCase))
                return text;
        }

        return null;
    }

    private static void Listen()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                        Callback?.Invoke(text.Trim());
                }
                catch
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
        });
    }

    private static void Send(string callback)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.Write(callback);
        }
        catch
        {
            // 主窗口没接上时，这次回调丢掉，用户可以再点一次登录。
        }
    }
}
