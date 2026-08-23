using System.IO;
using System.IO.Pipes;
using SysMon.Core.Diagnostics;

namespace SysMon.App.Services;

/// <summary>
/// Lets a second launch bring the running instance's window to the front instead of starting
/// another monitor. A named pipe is used rather than a window message so it works regardless of
/// whether the first instance currently has a visible window.
/// </summary>
public static class SingleInstance
{
    private const string PipeName = "PulseMonitor.SingleInstance";

    /// <summary>Starts listening for the "show yourself" signal from later launches.</summary>
    public static void Listen(Action onSignal)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    server.WaitForConnection();
                    onSignal();
                }
                catch (Exception ex)
                {
                    Log.Once("singleinstance:listen", LogLevel.Warn, "The single-instance listener failed.", ex);

                    // Back off rather than spinning if the pipe cannot be created at all.
                    Thread.Sleep(5000);
                }
            }
        })
        {
            IsBackground = true,
            Name = "SysMon.SingleInstance",
        };

        thread.Start();
    }

    /// <summary>Signals the already-running instance. Failure is harmless: the new process just exits.</summary>
    public static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not signal the existing instance.", ex);
        }
    }
}
