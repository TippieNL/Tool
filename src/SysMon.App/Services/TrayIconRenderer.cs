using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using SysMon.Core.Diagnostics;

namespace SysMon.App.Services;

/// <summary>
/// Renders a number into a tray-sized icon.
///
/// Two details matter here. The rendered value is cached, so the icon is only redrawn when the
/// displayed integer actually changes rather than once per tick. And every GDI handle is released
/// explicitly: an icon created from a bitmap owns an HICON that Windows will not collect on its
/// own, and leaking one per second is the classic way a tray app grows without bound.
/// </summary>
public sealed partial class TrayIconRenderer : IDisposable
{
    private const int IconSize = 32;

    private readonly Bitmap _bitmap = new(IconSize, IconSize, PixelFormat.Format32bppArgb);
    private readonly Font _font = new("Segoe UI", 19, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _smallFont = new("Segoe UI", 15, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);

    private string? _lastText;
    private Color _lastColor;
    private BitmapSource? _cached;
    private bool _disposed;

    /// <summary>
    /// Returns an icon showing the given text, reusing the previous one when nothing has changed.
    /// Text longer than two characters is drawn in a smaller face so "100" still fits.
    /// </summary>
    public BitmapSource? Render(string text, Color color)
    {
        if (_disposed)
        {
            return null;
        }

        if (_cached is not null && string.Equals(text, _lastText, StringComparison.Ordinal) && color == _lastColor)
        {
            return _cached;
        }

        try
        {
            using (var graphics = Graphics.FromImage(_bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                var font = text.Length > 2 ? _smallFont : _font;
                var size = graphics.MeasureString(text, font);

                using var brush = new SolidBrush(color);
                graphics.DrawString(
                    text,
                    font,
                    brush,
                    (IconSize - size.Width) / 2f,
                    (IconSize - size.Height) / 2f);
            }

            _cached = ToBitmapSource(_bitmap);
            _lastText = text;
            _lastColor = color;

            return _cached;
        }
        catch (Exception ex)
        {
            Log.Once("tray:render", LogLevel.Warn, "Could not render the tray icon.", ex);
            return _cached;
        }
    }

    /// <summary>
    /// Converts the GDI bitmap into a frozen WPF image source, releasing the intermediate
    /// HBITMAP. Freezing lets the tray icon be assigned from any thread.
    /// </summary>
    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap(Color.FromArgb(0));

        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bitmap.Dispose();
        _font.Dispose();
        _smallFont.Dispose();
    }
}
