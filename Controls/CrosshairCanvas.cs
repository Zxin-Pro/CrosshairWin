using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CrosshairWin.Models;

namespace CrosshairWin.Controls;

/// <summary>
/// Renders a <see cref="CrosshairPreset"/> into a square drawing surface.
///
/// The control is deliberately dumb: it draws a crosshair centred in its own bounds
/// and knows nothing about monitors or Win32. The overlay window supplies sizing and
/// position; the settings preview reuses the same control, so what the user sees in
/// the preview is exactly what is drawn on screen.
/// </summary>
public sealed class CrosshairCanvas : FrameworkElement
{
    public static readonly DependencyProperty PresetProperty =
        DependencyProperty.Register(
            nameof(Preset),
            typeof(CrosshairPreset),
            typeof(CrosshairCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Monitor height in physical pixels, used when the preset scales by screen percentage.
    /// </summary>
    public static readonly DependencyProperty TargetScreenHeightProperty =
        DependencyProperty.Register(
            nameof(TargetScreenHeight),
            typeof(double),
            typeof(CrosshairCanvas),
            new FrameworkPropertyMetadata(1080.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Set by the host when image loading failed, to draw a fallback marker.</summary>
    public static readonly DependencyProperty FallbackActiveProperty =
        DependencyProperty.Register(
            nameof(FallbackActive),
            typeof(bool),
            typeof(CrosshairCanvas),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public CrosshairPreset? Preset
    {
        get => (CrosshairPreset?)GetValue(PresetProperty);
        set => SetValue(PresetProperty, value);
    }

    public double TargetScreenHeight
    {
        get => (double)GetValue(TargetScreenHeightProperty);
        set => SetValue(TargetScreenHeightProperty, value);
    }

    public bool FallbackActive
    {
        get => (bool)GetValue(FallbackActiveProperty);
        set => SetValue(FallbackActiveProperty, value);
    }

    /// <summary>
    /// Supplied by the host so image presets can be drawn. Returning null means
    /// "unavailable", which triggers the built-in cross fallback.
    /// </summary>
    public Func<CrosshairPreset, ImageSource?>? ImageProvider { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var preset = Preset;
        if (preset is null)
            return;

        // The element is transparent; never paint an opaque background here or the
        // overlay would stop being see-through.
        var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);

        if (center.X <= 0 || center.Y <= 0)
            return;

        try
        {
            if (preset.Shape == CrosshairShape.Image)
                DrawImage(dc, preset, center);
            else
                DrawBuiltIn(dc, preset, center);
        }
        catch
        {
            // A rendering failure must never tear down the overlay; draw a plain
            // cross instead so the user still sees something usable.
            DrawFallbackCross(dc, center);
        }
    }

    // ------------------------------------------------------------ built-ins

    private static void DrawBuiltIn(DrawingContext dc, CrosshairPreset preset, Point center)
    {
        double opacity = Math.Clamp(preset.Opacity, 0, 1);
        var brush = CreateBrush(preset.Color, opacity);

        Geometry? outlineGeometry = null;
        var outlinePen = preset.Outline
            ? CreatePen(preset.OutlineColor, opacity, preset.Thickness + 2)
            : null;
        var pen = CreatePen(preset.Color, opacity, preset.Thickness);

        double half = preset.Size / 2.0;
        double gap = Math.Max(0, preset.Gap);

        switch (preset.Shape)
        {
            case CrosshairShape.Cross:
            {
                var geometry = new GeometryGroup();
                // Horizontal bar, split by the centre gap so the middle stays clear.
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X - half, center.Y - preset.Thickness / 2.0,
                             Math.Max(0, half - gap), preset.Thickness)));
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X + gap, center.Y - preset.Thickness / 2.0,
                             Math.Max(0, half - gap), preset.Thickness)));
                // Vertical bar.
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X - preset.Thickness / 2.0, center.Y - half,
                             preset.Thickness, Math.Max(0, half - gap))));
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X - preset.Thickness / 2.0, center.Y + gap,
                             preset.Thickness, Math.Max(0, half - gap))));

                DrawWithOptionalOutline(dc, geometry, pen, outlinePen, ref outlineGeometry);
                break;
            }

            case CrosshairShape.Dot:
            {
                double r = Math.Max(0.5, preset.Radius);
                double d = r * 2;
                dc.DrawEllipse(brush, null, center, r, r);

                if (preset.Outline)
                    dc.DrawEllipse(null, outlinePen, center, r + 1, r + 1);

                _ = d;
                break;
            }

            case CrosshairShape.Circle:
            {
                double r = Math.Max(0.5, preset.Radius);
                dc.DrawEllipse(null, pen, center, r, r);

                if (preset.Outline)
                    dc.DrawEllipse(null, outlinePen, center, r, r);
                break;
            }

            case CrosshairShape.TShape:
            {
                var geometry = new GeometryGroup();
                // Horizontal top bar.
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X - half, center.Y - preset.Thickness / 2.0,
                             Math.Max(0, half - gap), preset.Thickness)));
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X + gap, center.Y - preset.Thickness / 2.0,
                             Math.Max(0, half - gap), preset.Thickness)));
                // Single downward stem.
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X - preset.Thickness / 2.0, center.Y + gap,
                             preset.Thickness, Math.Max(0, half - gap))));

                DrawWithOptionalOutline(dc, geometry, pen, outlinePen, ref outlineGeometry);
                break;
            }

            case CrosshairShape.CrossDot:
            {
                var geometry = new GeometryGroup();
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X - half, center.Y - preset.Thickness / 2.0,
                             Math.Max(0, half - gap), preset.Thickness)));
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X + gap, center.Y - preset.Thickness / 2.0,
                             Math.Max(0, half - gap), preset.Thickness)));
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X - preset.Thickness / 2.0, center.Y - half,
                             preset.Thickness, Math.Max(0, half - gap))));
                geometry.Children.Add(new RectangleGeometry(
                    new Rect(center.X - preset.Thickness / 2.0, center.Y + gap,
                             preset.Thickness, Math.Max(0, half - gap))));

                DrawWithOptionalOutline(dc, geometry, pen, outlinePen, ref outlineGeometry);

                double r = Math.Max(0.5, preset.Radius);
                dc.DrawEllipse(brush, null, center, r, r);
                break;
            }

            default:
                DrawFallbackCross(dc, center);
                break;
        }
    }

    /// <summary>
    /// Draws the outline pass first (slightly thicker), then the fill pass on top.
    /// This gives the crosshair contrast against both light and dark backgrounds.
    /// </summary>
    private static void DrawWithOptionalOutline(
        DrawingContext dc, Geometry geometry, Pen pen, Pen? outlinePen, ref Geometry? outlineDummy)
    {
        _ = outlineDummy;

        if (outlinePen is not null)
            dc.DrawGeometry(null, outlinePen, geometry);

        dc.DrawGeometry(null, pen, geometry);
    }

    // --------------------------------------------------------------- images

    private void DrawImage(DrawingContext dc, CrosshairPreset preset, Point center)
    {
        var image = ImageProvider?.Invoke(preset);

        if (image is null)
        {
            // Image unavailable/corrupt -> documented fallback to the built-in cross.
            FallbackActive = true;
            DrawFallbackCross(dc, center);
            return;
        }

        FallbackActive = false;

        double naturalWidth = image.Width;
        double naturalHeight = image.Height;
        if (naturalWidth <= 0 || naturalHeight <= 0)
        {
            DrawFallbackCross(dc, center);
            return;
        }

        // ---- resolve the target size -------------------------------------
        double targetWidth;
        double targetHeight;

        if (preset.ImageScaleMode == ImageScaleMode.ScreenPercent)
        {
            // Height is a percentage of the monitor's physical height, which makes the
            // crosshair feel identical across 1080p / 1440p / 4K displays.
            targetHeight = Math.Max(1, TargetScreenHeight * (preset.ImageHeightPercent / 100.0));
            targetWidth = preset.ImageKeepAspectRatio
                ? targetHeight * (naturalWidth / naturalHeight)
                : targetHeight * (naturalWidth / naturalHeight);
        }
        else
        {
            targetWidth = Math.Max(1, preset.ImageWidthPx);
            targetHeight = preset.ImageKeepAspectRatio
                ? targetWidth * (naturalHeight / naturalWidth)
                : targetWidth;
        }

        // ---- transform ---------------------------------------------------
        double opacity = Math.Clamp(preset.ImageOpacity, 0, 1);
        double offsetX = preset.ImageOffsetX;
        double offsetY = preset.ImageOffsetY;

        dc.PushOpacity(opacity);

        var transform = new TransformGroup();

        if (Math.Abs(preset.ImageRotation) > 0.01)
        {
            // Rotate about the centre point of the drawn image.
            transform.Children.Add(new RotateTransform(preset.ImageRotation, center.X + offsetX, center.Y + offsetY));
        }

        dc.PushTransform(transform);

        var rect = new Rect(
            center.X + offsetX - targetWidth / 2.0,
            center.Y + offsetY - targetHeight / 2.0,
            targetWidth,
            targetHeight);

        if (preset.ImageUseOriginalColors)
        {
            dc.DrawImage(image, rect);
        }
        else
        {
            // Tinted mode: draw the image through an opacity mask so its alpha shape is
            // preserved but every opaque pixel takes the chosen colour.
            var tintBrush = CreateBrush(preset.ImageTintColor, 1.0);
            var visualBrush = new ImageBrush(image)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            visualBrush.Freeze();

            var drawing = new GeometryDrawing(tintBrush, null, new RectangleGeometry(rect));
            drawing.Freeze();

            dc.PushOpacityMask(visualBrush);
            dc.DrawDrawing(drawing);
            dc.Pop();
        }

        dc.Pop(); // transform
        dc.Pop(); // opacity
    }

    /// <summary>
    /// The guaranteed-visible fallback: a simple red cross.
    /// Used when an image is missing, corrupt, or decoding throws.
    /// </summary>
    private static void DrawFallbackCross(DrawingContext dc, Point center)
    {
        var brush = new SolidColorBrush(Color.FromArgb(255, 255, 59, 48));
        brush.Freeze();

        const double half = 14;
        const double thickness = 2;

        dc.DrawRectangle(brush, null,
            new Rect(center.X - half, center.Y - thickness / 2, half * 2, thickness));
        dc.DrawRectangle(brush, null,
            new Rect(center.X - thickness / 2, center.Y - half, thickness, half * 2));
    }

    // -------------------------------------------------------------- helpers

    /// <summary>Parses "#AARRGGBB", "#RRGGBB" or a named colour into a frozen brush.</summary>
    internal static SolidColorBrush CreateBrush(string? color, double opacity)
    {
        var parsed = ParseColor(color);
        var brush = new SolidColorBrush(parsed)
        {
            Opacity = Math.Clamp(opacity, 0, 1)
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Creates a frozen pen of the given thickness.</summary>
    internal static Pen CreatePen(string? color, double opacity, double thickness)
    {
        var brush = CreateBrush(color, opacity);
        var pen = new Pen(brush, Math.Max(0.5, thickness));
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// Robust colour parser. Falls back to red rather than throwing, because a bad
    /// colour string must never crash rendering.
    /// </summary>
    internal static Color ParseColor(string? value)
    {
        var fallback = Color.FromRgb(0xFF, 0x3B, 0x30);

        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        try
        {
            if (value.StartsWith('#'))
            {
                string hex = value[1..];

                // Normalise "#RGB" / "#ARGB" to the 6/8-digit forms.
                if (hex.Length == 3)
                {
                    hex = string.Concat(hex.Select(c => new string(c, 2)));
                }
                else if (hex.Length == 4)
                {
                    hex = string.Concat(hex.Select(c => new string(c, 2)));
                }

                if (hex.Length == 6)
                {
                    return Color.FromRgb(
                        byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }

                if (hex.Length == 8)
                {
                    return Color.FromArgb(
                        byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }
            }

            // Named colour or "#RRGGBB" handled by WPF's converter.
            var converted = ColorConverter.ConvertFromString(value);
            if (converted is Color c)
                return c;
        }
        catch
        {
            // fall through
        }

        return fallback;
    }
}
