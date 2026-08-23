using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace SysMon.App.Tests;

/// <summary>
/// Runs work on a single long-lived STA thread with a WPF <see cref="Application"/> and the app's
/// own theme loaded.
///
/// WPF visuals can only be created on an STA thread, and only one Application may exist per
/// process, so the thread and the Application are created once and shared by every test.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lock Gate = new();
    private static Dispatcher? _dispatcher;

    /// <summary>Executes <paramref name="action"/> on the UI thread and rethrows anything it throws.</summary>
    public static void Run(Action action)
    {
        var dispatcher = EnsureStarted();

        Exception? failure = null;

        dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (failure is not null)
        {
            // Preserve the original stack rather than wrapping, so a failure reads naturally.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static Dispatcher EnsureStarted()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;

            var thread = new Thread(() =>
            {
                _ = new Application
                {
                    // Nothing in the tests opens a window, so the app must not try to exit.
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                };

                LoadTheme();

                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "WpfTestHost",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("The WPF test host thread failed to start.");
            }

            _dispatcher = dispatcher;
            return _dispatcher!;
        }
    }

    /// <summary>
    /// Merges the application's theme dictionary. Without it every StaticResource lookup in the
    /// views throws at load time, which would mask the binding failures these tests look for.
    /// </summary>
    private static void LoadTheme()
    {
        var themeUri = new Uri("pack://application:,,,/PulseMonitor;component/Themes/Theme.xaml", UriKind.Absolute);
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });
    }
}

/// <summary>
/// Captures WPF data-binding failures while a scope is active.
///
/// WPF reports a broken binding by writing to a trace source and then rendering nothing, which is
/// why a binding typo looks like a blank label rather than an error. Listening to that source is
/// the only way to turn those silent failures into test failures.
/// </summary>
internal sealed class BindingErrorScope : IDisposable
{
    private readonly CollectingListener _listener = new();
    private readonly SourceLevels _previousLevel;

    public BindingErrorScope()
    {
        PresentationTraceSources.Refresh();

        var source = PresentationTraceSources.DataBindingSource;
        _previousLevel = source.Switch.Level;
        source.Switch.Level = SourceLevels.Error | SourceLevels.Warning;
        source.Listeners.Add(_listener);
    }

    public IReadOnlyList<string> Errors => _listener.Messages;

    public void Dispose()
    {
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Remove(_listener);
        source.Switch.Level = _previousLevel;
    }

    private sealed class CollectingListener : TraceListener
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _messages.Add(message);
            }
        }
    }
}
