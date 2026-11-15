using System.Collections.Concurrent;
using System.Text;

namespace NotchDock;

/// <summary>
/// 异步日志。写日志绝不能阻塞 UI 线程（悬停手势里会调用），
/// 所以统一入队，由后台线程批量落盘。
/// </summary>
public static class Log
{
    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static string LogPath => Path.Combine(Store.Dir, "notchdock.log");

    static Log()
    {
        var t = new Thread(Pump)
        {
            IsBackground = true,
            Name = "NotchDock.Log"
        };
        t.Start();
    }

    public static void Ex(string tag, Exception ex)
        => Write($"{tag}: {ex.GetType().Name} {ex.Message}");

    public static void Write(string msg)
    {
        Queue.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        try { Signal.Set(); } catch { }
    }

    private static void Pump()
    {
        while (true)
        {
            try
            {
                Signal.WaitOne();
                Thread.Sleep(300);              // 合并同一波日志

                var sb = new StringBuilder();
                while (Queue.TryDequeue(out var line)) sb.AppendLine(line);
                if (sb.Length == 0) continue;

                Directory.CreateDirectory(Store.Dir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                    File.Delete(LogPath);

                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
