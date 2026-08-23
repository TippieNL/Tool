using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SysMon.Core.History;

namespace SysMon.App.Controls;

/// <summary>
/// Draws a history series as a filled line chart.
///
/// This is a bare FrameworkElement with an OnRender override rather than a charting control: the
/// whole render is one StreamGeometry built from a float span, with frozen brushes and pens, so a
/// graph costs a few dozen microseconds and allocates almost nothing. Redraws happen only when
/// <see cref="Refresh"/> is called, which the view model does once per tick.
///
/// Gaps (NaN samples, meaning the sensor was unavailable) break the line instead of being drawn
/// as a drop to zero, so a missing sensor never looks like idle hardware.
/// </summary>
public sealed class HistoryGraph : FrameworkElement
{
    private static readonly Pen GridPen = CreateFrozenPen(Color.FromArgb(28, 255, 255, 255), 1);

    private float[] _samples = [];
    private int _sampleCount;
    private Pen? _linePen;
    private Brush? _fillBrush;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(HistorySeries), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The data to draw. Null renders an empty frame rather than throwing.</summary>
    public HistorySeries? Series
    {
        get => (HistorySeries?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public static readonly DependencyProperty LineColorProperty = DependencyProperty.Register(
        nameof(LineColor), typeof(Color), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(Colors.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender, OnLineColorChanged));

    public Color LineColor
    {
        get => (Color)GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fixed upper bound. Ignored when <see cref="AutoScale"/> is set.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Scales the vertical axis to the largest sample in view. Used for unbounded quantities such
    /// as network throughput, where a fixed maximum would leave the line flat on the floor.
    /// </summary>
    public bool AutoScale
    {
        get => (bool)GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(1.5d, FrameworkPropertyMetadataOptions.AffectsRender, OnLineColorChanged));

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Repaints from the series' current contents. Called once per tick by the owner.</summary>
    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 1 || height <= 1)
        {
            return;
        }

        if (ShowGrid)
        {
            DrawGrid(dc, width, height);
        }

        var series = Series;
        if (series is null || series.Count == 0)
        {
            return;
        }

        // Reuse the sample buffer across renders; it only grows when the series does.
        if (_samples.Length < series.Capacity)
        {
            _samples = new float[series.Capacity];
        }

        _sampleCount = series.CopyTo(_samples);
        if (_sampleCount < 2)
        {
            return;
        }

        var min = Minimum;
        var max = ResolveMaximum(series, min);
        var range = max - min;

        if (range <= 0)
        {
            return;
        }

        EnsureRenderResources();

        // The series is drawn right-aligned: the newest sample sits at the right edge, and a
        // partially-filled buffer leaves empty space on the left rather than stretching.
        var capacity = Math.Max(series.Capacity, 2);
        var stepX = width / (capacity - 1);
        var offsetX = width - ((_sampleCount - 1) * stepX);

        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            var inSegment = false;
            var segmentStartX = 0d;
            var lastX = 0d;

            for (var i = 0; i < _sampleCount; i++)
            {
                var sample = _samples[i];
                var x = offsetX + (i * stepX);

                if (float.IsNaN(sample))
                {
                    // Gap: close off the current filled segment and wait for real data.
                    if (inSegment)
                    {
                        CloseSegment(ctx, lastX, segmentStartX, height);
                        inSegment = false;
                    }

                    continue;
                }

                var normalized = Math.Clamp((sample - min) / range, 0, 1);
                var y = height - (normalized * height);

                if (!inSegment)
                {
                    ctx.BeginFigure(new Point(x, y), isFilled: true, isClosed: false);
                    segmentStartX = x;
                    inSegment = true;
                }
                else
                {
                    ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: false);
                }

                lastX = x;
            }

            if (inSegment)
            {
                CloseSegment(ctx, lastX, segmentStartX, height);
            }
        }

        geometry.Freeze();

        dc.DrawGeometry(_fillBrush, _linePen, geometry);
    }

    /// <summary>
    /// Drops the figure to the baseline and back so the area under the line is filled. The final
    /// two points are unstroked, which keeps the fill without drawing a box around it.
    /// </summary>
    private static void CloseSegment(StreamGeometryContext ctx, double lastX, double startX, double height)
    {
        ctx.LineTo(new Point(lastX, height), isStroked: false, isSmoothJoin: false);
        ctx.LineTo(new Point(startX, height), isStroked: false, isSmoothJoin: false);
    }

    private double ResolveMaximum(HistorySeries series, double min)
    {
        if (!AutoScale)
        {
            return Maximum;
        }

        var peak = series.Max() ?? 0;

        // Round the peak up to something stable so the axis does not jitter on every sample,
        // and keep a floor so an idle network does not amplify noise into a full-height graph.
        var headroom = Math.Max(peak * 1.25, min + 1);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(headroom, 1))));

        return Math.Ceiling(headroom / magnitude) * magnitude;
    }

    private void DrawGrid(DrawingContext dc, double width, double height)
    {
        for (var i = 1; i < 4; i++)
        {
            var y = Math.Round(height * i / 4) + 0.5;
            dc.DrawLine(GridPen, new Point(0, y), new Point(width, y));
        }
    }

    private void EnsureRenderResources()
    {
        if (_linePen is not null && _fillBrush is not null)
        {
            return;
        }

        var color = LineColor;

        _linePen = CreateFrozenPen(color, StrokeThickness);

        // A vertical fade under the line: solid-ish at the line, transparent at the baseline.
        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(70, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            ],
        };

        fill.Freeze();
        _fillBrush = fill;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var pen = new Pen(brush, thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        pen.Freeze();
        return pen;
    }

    private static void OnLineColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (HistoryGraph)d;
        graph._linePen = null;
        graph._fillBrush = null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Fill whatever the parent offers; the graph has no intrinsic size.
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(width, height);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"HistoryGraph({Series?.Id ?? "none"})");
}
