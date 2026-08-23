using System.Collections.Concurrent;
using System.Text;

namespace SysMon.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
    Off = 4,
}

/// <summary>
/// A deliberately small rolling-file logger. Writing happens on a background thread so that a
/// slow disk can never stall a monitoring tick, and <see cref="Once"/> collapses the
/// "this sensor is missing" case down to a single line instead of one per tick forever.
/// </summary>
public static class Log
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxBacklog = 4096;

    private static readonly BlockingCollection<string> Queue = new(MaxBacklog);
    private static readonly ConcurrentDictionary<string, byte> SeenKeys = new(StringComparer.Ordinal);
    private static readonly object FileLock = new();

    private static Thread? _writer;
    private static string? _path;
    private static volatile bool _started;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static string? FilePath => _path;

    /// <summary>Starts the background writer. Safe to call more than once.</summary>
    public static void Start(string filePath)
    {
        lock (FileLock)
        {
            if (_started)
            {
                return;
            }

            _path = filePath;
            _started = true;

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch (Exception)
            {
                // A logger that cannot create its own directory must not take the app down.
                _path = null;
                return;
            }

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "SysMon.Log",
                Priority = ThreadPriority.BelowNormal,
            };
            _writer.Start();
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);

    public static void Info(string message) => Write(LogLevel.Info, message, null);

    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);

    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    /// <summary>
    /// Logs a message the first time a given key is seen and never again. Used for permanent
    /// conditions such as an absent sensor, which would otherwise flood the log every tick.
    /// </summary>
    public static void Once(string key, LogLevel level, string message, Exception? ex = null)
    {
        if (SeenKeys.TryAdd(key, 0))
        {
            Write(level, message, ex);
        }
    }

    /// <summary>Allows a previously-logged <see cref="Once"/> key to log again, e.g. after recovery.</summary>
    public static void ResetOnce(string key) => SeenKeys.TryRemove(key, out _);

    public static void Flush()
    {
        // Best effort: give the writer thread a moment to drain before the process exits.
        for (var i = 0; i < 20 && Queue.Count > 0; i++)
        {
            Thread.Sleep(10);
        }
    }

    private static void Write(LogLevel level, string message, Exception? ex)
    {
        if (level < MinimumLevel || !_started || _path is null)
        {
            return;
        }

        var sb = new StringBuilder(message.Length + 64);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
          .Append(message);

        if (ex is not null)
        {
            sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        }

        // Never block a monitoring tick on the log: if the backlog is full, drop the line.
        Queue.TryAdd(sb.ToString());
    }

    private static void WriterLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                var path = _path;
                if (path is null)
                {
                    continue;
                }

                RollIfNeeded(path);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging failures are swallowed by design. There is nowhere left to report them.
            }
        }
    }

    private static void RollIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxFileBytes)
        {
            return;
        }

        // Keep app.log, app.1.log, app.2.log and discard anything older.
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        var oldest = Path.Combine(dir, $"{name}.2{ext}");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        var middle = Path.Combine(dir, $"{name}.1{ext}");
        if (File.Exists(middle))
        {
            File.Move(middle, oldest);
        }

        File.Move(path, middle);
    }
}
