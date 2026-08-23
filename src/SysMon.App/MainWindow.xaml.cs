using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using H.NotifyIcon;
using SysMon.App.Services;
using SysMon.App.ViewModels;
using SysMon.Core.Configuration;
using SysMon.Core.Diagnostics;
using SysMon.Core.Formatting;

namespace SysMon.App;

public partial class MainWindow : Window
{
    private readonly TrayIconRenderer _trayRenderer = new();

    private MainViewModel? _viewModel;
    private bool _reallyClosing;

    /// <summary>Always the live settings object, never a stale copy taken at startup.</summary>
    private AppSettings Settings => _viewModel?.CurrentSettings ?? DefaultSettings;

    private static readonly AppSettings DefaultSettings = new();

    public MainWindow() => InitializeComponent();

    internal void Initialize(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.SnapshotApplied += OnSnapshotApplied;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        StateChanged += OnStateChanged;
        IsVisibleChanged += OnIsVisibleChanged;

        // The tray icon is only created on demand by the library; force it so a start-minimised
        // launch still has somewhere to appear.
        TrayIcon?.ForceCreate(enablesEfficiencyMode: false);
        UpdateTrayIcon();
    }

    internal void RestoreFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// Repaints the graphs once per tick, after the cards have been updated. Doing it here rather
    /// than through bindings means one invalidation pass for the whole window.
    /// </summary>
    private void OnSnapshotApplied()
    {
        foreach (var graph in FindGraphs(this))
        {
            graph.Refresh();
        }

        UpdateTrayIcon();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsMonitoring))
        {
            TrayMonitoringItem.Header = _viewModel?.IsMonitoring == true ? "Pause monitoring" : "Start monitoring";
        }
    }

    /// <summary>Walks the visual tree for graphs. The tree is small, and this runs once per tick.</summary>
    private static IEnumerable<Controls.HistoryGraph> FindGraphs(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is Controls.HistoryGraph graph)
            {
                yield return graph;
            }
            else
            {
                foreach (var nested in FindGraphs(child))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// Draws the current stat into the tray icon. The renderer caches by displayed text, so this
    /// only costs anything when the number actually changes.
    /// </summary>
    private void UpdateTrayIcon()
    {
        var icon = TrayIcon;
        if (icon is null || _viewModel is null)
        {
            return;
        }

        if (!Settings.EnableTrayStats)
        {
            icon.ToolTipText = $"{AppPaths.DisplayName} — {_viewModel.Cpu.PrimaryValue} CPU";
            return;
        }

        var (text, value) = ResolveTrayStat();

        var color = value switch
        {
            >= 90 => System.Drawing.Color.FromArgb(245, 69, 92),
            >= 75 => System.Drawing.Color.FromArgb(245, 165, 36),
            _ => System.Drawing.Color.FromArgb(242, 244, 248),
        };

        var rendered = _trayRenderer.Render(text, color);
        if (rendered is not null)
        {
            icon.IconSource = rendered;
        }

        icon.ToolTipText = $"{AppPaths.DisplayName}\nCPU {_viewModel.Cpu.PrimaryValue} · {_viewModel.Cpu.Temperature}\nRAM {_viewModel.Memory.PrimaryValue}";
    }

    /// <summary>
    /// The text and severity value for the tray icon. Falls back to a dash when the chosen stat
    /// is unavailable, e.g. GPU temperature on a machine with no GPU sensors.
    /// </summary>
    private (string Text, double Value) ResolveTrayStat()
    {
        var gpu = _viewModel?.Gpus.FirstOrDefault();

        var raw = Settings.TrayStat switch
        {
            TrayStat.CpuLoad => _viewModel?.Cpu.BarValue,
            TrayStat.CpuTemperature => ParseLeadingNumber(_viewModel?.Cpu.Temperature),
            TrayStat.MemoryLoad => _viewModel?.Memory.BarValue,
            TrayStat.GpuLoad => gpu?.BarValue,
            TrayStat.GpuTemperature => ParseLeadingNumber(gpu?.Temperature),
            _ => _viewModel?.Cpu.BarValue,
        };

        return raw is { } value
            ? (Math.Round(value).ToString("F0", System.Globalization.CultureInfo.InvariantCulture), value)
            : ("--", 0);
    }

    private static double? ParseLeadingNumber(string? formatted)
    {
        if (string.IsNullOrEmpty(formatted) || formatted == Format.NotAvailable)
        {
            return null;
        }

        var end = 0;
        while (end < formatted.Length && (char.IsAsciiDigit(formatted[end]) || formatted[end] == '-'))
        {
            end++;
        }

        return end > 0 && double.TryParse(formatted[..end], System.Globalization.CultureInfo.CurrentCulture, out var value)
            ? value
            : null;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && Settings.MinimizeToTray)
        {
            Hide();
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        _viewModel?.SetUiVisible(IsVisible && WindowState != WindowState.Minimized);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing && Settings.CloseToTray)
        {
            // Closing to tray keeps monitoring alive, which is the point of a tray monitor.
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnTrayLeftClick(object sender, RoutedEventArgs e)
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            Hide();
        }
        else
        {
            RestoreFromTray();
        }
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void OnTrayToggleMonitoring(object sender, RoutedEventArgs e) =>
        _viewModel?.ToggleMonitoringCommand.Execute(null);

    private void OnTraySettings(object sender, RoutedEventArgs e)
    {
        RestoreFromTray();
        _viewModel?.NavigateCommand.Execute(nameof(AppPage.Settings));
    }

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        Log.Info("Exit requested from the tray menu.");
        _reallyClosing = true;
        _trayRenderer.Dispose();
        TrayIcon?.Dispose();
        Application.Current.Shutdown();
    }
}
