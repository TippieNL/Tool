using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SysMon.App.Services;
using SysMon.App.ViewModels;
using SysMon.App.Views;
using SysMon.Core.Configuration;
using SysMon.Core.History;
using SysMon.Core.Models;
using SysMon.Monitoring;
using Xunit;

namespace SysMon.App.Tests;

/// <summary>
/// Renders the real views against a real view model and fails on any broken binding.
///
/// A broken WPF binding produces no exception and no visible error — it renders as an empty
/// string. That is how a page can look complete while showing nothing, so these tests exist to
/// turn silent binding failures into build failures.
/// </summary>
public class ViewBindingTests
{
    private static readonly Size Viewport = new(1280, 800);

    private static MainViewModel CreateViewModel()
    {
        var settings = new AppSettings();
        settings.Normalize();

        var history = new HistoryStore(HistoryStore.CapacityFor(
            TimeSpan.FromSeconds(settings.GraphHistorySeconds),
            TimeSpan.FromMilliseconds(settings.UpdateIntervalMs)));

        // Never started: these tests exercise the UI, not the hardware.
        var session = new HardwareSession();
        var monitoring = new MonitoringService(settings, session, history);

        var store = new SettingsStore(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sysmon-uitests-" + Guid.NewGuid().ToString("N"), "settings.json"));

        return new MainViewModel(
            monitoring,
            history,
            store,
            settings,
            new LogOnlyNotificationService(),
            Dispatcher.CurrentDispatcher);
    }

    /// <summary>
    /// Returns the window's content tree.
    ///
    /// A Window only realises its visual tree once it is shown, and a CI runner is no place to
    /// open windows, so the tests measure the content directly. That is still the real
    /// MainWindow.xaml markup, including the page host whose binding is under test.
    /// </summary>
    private static FrameworkElement ContentOf(Window window) => (FrameworkElement)window.Content;

    private static void Render(FrameworkElement element)
    {
        // A binding is only evaluated once its element is measured, so layout has to be forced.
        element.Measure(Viewport);
        element.Arrange(new Rect(Viewport));
        element.UpdateLayout();

        // Let bindings queued at lower priorities settle before the scope is inspected.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
    }

    private static void AssertNoBindingErrors(Func<FrameworkElement> factory)
    {
        WpfTestHost.Run(() =>
        {
            using var scope = new BindingErrorScope();

            var element = factory();
            element.DataContext = CreateViewModel();
            Render(element);

            Assert.True(
                scope.Errors.Count == 0,
                "WPF reported binding failures:" + Environment.NewLine +
                string.Join(Environment.NewLine, scope.Errors));
        });
    }

    [Fact]
    public void DashboardViewBindsCleanly() => AssertNoBindingErrors(static () => new DashboardView());

    [Fact]
    public void PerformanceViewBindsCleanly() => AssertNoBindingErrors(static () => new PerformanceView());

    [Fact]
    public void SensorsViewBindsCleanly() => AssertNoBindingErrors(static () => new SensorsView());

    [Fact]
    public void SystemViewBindsCleanly() => AssertNoBindingErrors(static () => new SystemView());

    [Fact]
    public void SettingsViewBindsCleanly() => AssertNoBindingErrors(static () => new SettingsView());

    /// <summary>
    /// The page host must pass its DataContext down to whichever page is showing. Leaving
    /// ContentControl.Content unset gives every page a null DataContext, which renders the static
    /// labels but blanks every value — the failure this test exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(AppPage.Dashboard)]
    [InlineData(AppPage.Performance)]
    [InlineData(AppPage.Sensors)]
    [InlineData(AppPage.System)]
    [InlineData(AppPage.Settings)]
    public void EveryPageReceivesTheViewModelAsItsDataContext(AppPage page)
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = CreateViewModel();
            viewModel.CurrentPage = page;

            var window = new MainWindow { DataContext = viewModel };
            var root = ContentOf(window);
            Render(root);

            Assert.True(root.DataContext is not null, "The test harness failed to supply a DataContext.");

            var view = FindDescendant<UserControl>(root);
            Assert.True(view is not null, $"No page was rendered for {page}.");

            // The invariant that broke: the page host must hand its own DataContext to the page.
            // Comparing against the host rather than the view model keeps this independent of how
            // the harness attaches the context.
            Assert.Same(root.DataContext, view!.DataContext);
        });
    }

    /// <summary>Renders the whole window on each page and requires every binding to resolve.</summary>
    [Theory]
    [InlineData(AppPage.Dashboard)]
    [InlineData(AppPage.Performance)]
    [InlineData(AppPage.Sensors)]
    [InlineData(AppPage.System)]
    [InlineData(AppPage.Settings)]
    public void MainWindowBindsCleanlyOnEveryPage(AppPage page)
    {
        WpfTestHost.Run(() =>
        {
            using var scope = new BindingErrorScope();

            var viewModel = CreateViewModel();
            viewModel.CurrentPage = page;

            var window = new MainWindow { DataContext = viewModel };
            Render(ContentOf(window));

            Assert.True(
                scope.Errors.Count == 0,
                $"WPF reported binding failures on the {page} page:" + Environment.NewLine +
                string.Join(Environment.NewLine, scope.Errors));
        });
    }

    /// <summary>
    /// With live data applied, the System page must show real values rather than empty strings.
    /// This is the symptom a user sees, asserted directly.
    /// </summary>
    [Fact]
    public void SystemPageShowsValuesOnceASnapshotIsApplied()
    {
        WpfTestHost.Run(() =>
        {
            var viewModel = CreateViewModel();
            viewModel.CurrentPage = AppPage.System;

            viewModel.System.Update(new Snapshot
            {
                System = new SystemInfo
                {
                    HostName = "TEST-PC",
                    OsName = "Windows 11",
                    OsVersion = "10.0.26100",
                    Uptime = TimeSpan.FromHours(3),
                    IsElevated = false,
                },
            });

            var window = new MainWindow { DataContext = viewModel };
            var root = ContentOf(window);
            Render(root);

            var rendered = FindDescendants<TextBlock>(root).Select(static t => t.Text).ToList();

            Assert.True(rendered.Count > 0, "Nothing rendered, so this asserts nothing.");

            Assert.Contains("TEST-PC", rendered);
            Assert.Contains(rendered, text => text.Contains("Windows 11", StringComparison.Ordinal));
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject =>
        FindDescendants<T>(root).FirstOrDefault();

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
