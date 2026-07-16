using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace OfflineMusicLibrary;

/// <summary>
/// Draws text with a real vector outline. Unlike a zero-depth drop shadow, the
/// outline does not blur the glyph fill and remains sharp in transparent windows.
/// </summary>
public sealed class OutlinedTextBlock : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontSize,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double size && double.IsFinite(size) && size > 0);

    public static readonly DependencyProperty FontStyleProperty = DependencyProperty.Register(
        nameof(FontStyle), typeof(FontStyle), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(FontStyles.Normal,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(FontWeights.Normal,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontStretchProperty = DependencyProperty.Register(
        nameof(FontStretch), typeof(FontStretch), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(FontStretches.Normal,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(Brushes.White,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(Brushes.Transparent,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(0d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double thickness && double.IsFinite(thickness) && thickness >= 0);

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(TextAlignment.Left,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(TextWrapping.NoWrap,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
        nameof(TextTrimming), typeof(TextTrimming), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(TextTrimming.None,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public OutlinedTextBlock()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontStyle FontStyle
    {
        get => (FontStyle)GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public FontStretch FontStretch
    {
        get => (FontStretch)GetValue(FontStretchProperty);
        set => SetValue(FontStretchProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public TextTrimming TextTrimming
    {
        get => (TextTrimming)GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (string.IsNullOrEmpty(Text))
            return new Size(0, 0);

        var inset = OutlineInset;
        var textWidth = double.IsFinite(availableSize.Width)
            ? Math.Max(0.1, availableSize.Width - inset * 2)
            : 1_000_000;
        var textHeight = double.IsFinite(availableSize.Height)
            ? Math.Max(0.1, availableSize.Height - inset * 2)
            : 1_000_000;
        var formatted = CreateFormattedText(textWidth, textHeight);

        var desiredWidth = Math.Ceiling(formatted.WidthIncludingTrailingWhitespace + inset * 2);
        var desiredHeight = Math.Ceiling(formatted.Height + inset * 2);
        if (double.IsFinite(availableSize.Width))
            desiredWidth = Math.Min(desiredWidth, availableSize.Width);
        if (double.IsFinite(availableSize.Height))
            desiredHeight = Math.Min(desiredHeight, availableSize.Height);
        return new Size(Math.Max(0, desiredWidth), Math.Max(0, desiredHeight));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (string.IsNullOrEmpty(Text) || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var inset = OutlineInset;
        var width = Math.Max(0.1, ActualWidth - inset * 2);
        var height = Math.Max(0.1, ActualHeight - inset * 2);
        var formatted = CreateFormattedText(width, height);
        var origin = GetTextOrigin(formatted, inset);

        if (StrokeThickness > 0 && Stroke is { } stroke && stroke.Opacity > 0)
        {
            var geometry = formatted.BuildGeometry(origin);
            var pen = new Pen(stroke, StrokeThickness)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (pen.CanFreeze)
                pen.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        drawingContext.DrawText(formatted, origin);
    }

    /// <summary>
    /// Tests the rendered glyph outlines rather than this element's stretched layout box.
    /// This keeps desktop lyrics clickable without turning the whole transparent row into
    /// an invisible mouse shield.
    /// </summary>
    public bool ContainsRenderedText(Point point, double tolerance = 4)
    {
        if (string.IsNullOrEmpty(Text) || ActualWidth <= 0 || ActualHeight <= 0 ||
            point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
            return false;

        var inset = OutlineInset;
        var formatted = CreateFormattedText(
            Math.Max(0.1, ActualWidth - inset * 2),
            Math.Max(0.1, ActualHeight - inset * 2));
        var geometry = formatted.BuildGeometry(GetTextOrigin(formatted, inset));
        if (geometry.FillContains(point))
            return true;

        if (tolerance <= 0)
            return false;
        var hitPen = new Pen(Brushes.Black, tolerance * 2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        if (hitPen.CanFreeze)
            hitPen.Freeze();
        return geometry.StrokeContains(hitPen, point);
    }

    private Point GetTextOrigin(FormattedText formatted, double inset)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(
            RoundToPixel(inset, dpi.DpiScaleX),
            RoundToPixel(Math.Max(inset, (ActualHeight - formatted.Height) / 2), dpi.DpiScaleY));
    }

    private double OutlineInset => StrokeThickness > 0 ? StrokeThickness / 2 + 0.5 : 0;

    private FormattedText CreateFormattedText(double maxWidth, double maxHeight)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var formatted = new FormattedText(
            Text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            typeface,
            FontSize,
            Foreground,
            null,
            TextFormattingMode.Display,
            dpi.PixelsPerDip)
        {
            TextAlignment = TextAlignment,
            Trimming = TextTrimming,
            MaxTextWidth = Math.Max(0.1, maxWidth),
            MaxTextHeight = Math.Max(0.1, maxHeight)
        };

        if (TextWrapping == TextWrapping.NoWrap)
            formatted.MaxLineCount = 1;
        return formatted;
    }

    private static double RoundToPixel(double value, double scale) =>
        scale > 0 ? Math.Round(value * scale) / scale : value;
}
