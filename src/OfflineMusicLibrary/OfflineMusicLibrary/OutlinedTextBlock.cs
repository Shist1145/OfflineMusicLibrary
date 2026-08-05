using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace OfflineMusicLibrary;

public sealed class OutlinedTextBlock : FrameworkElement
{
	public static readonly DependencyProperty TextProperty = DependencyProperty.Register("Text", typeof(string), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register("FontFamily", typeof(FontFamily), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register("FontSize", typeof(double), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender), (object value) => value is double num && double.IsFinite(num) && num > 0.0);

	public static readonly DependencyProperty FontStyleProperty = DependencyProperty.Register("FontStyle", typeof(FontStyle), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(FontStyles.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register("FontWeight", typeof(FontWeight), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontStretchProperty = DependencyProperty.Register("FontStretch", typeof(FontStretch), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(FontStretches.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register("Foreground", typeof(Brush), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));

	public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(Brush), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));

	public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register("StrokeThickness", typeof(double), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender), (object value) => value is double num && double.IsFinite(num) && num >= 0.0);

	public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register("TextAlignment", typeof(TextAlignment), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register("TextWrapping", typeof(TextWrapping), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(TextWrapping.NoWrap, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register("TextTrimming", typeof(TextTrimming), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(TextTrimming.None, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public string Text
	{
		get
		{
			return (string)GetValue(TextProperty);
		}
		set
		{
			SetValue(TextProperty, value);
		}
	}

	public FontFamily FontFamily
	{
		get
		{
			return (FontFamily)GetValue(FontFamilyProperty);
		}
		set
		{
			SetValue(FontFamilyProperty, value);
		}
	}

	public double FontSize
	{
		get
		{
			return (double)GetValue(FontSizeProperty);
		}
		set
		{
			SetValue(FontSizeProperty, value);
		}
	}

	public FontStyle FontStyle
	{
		get
		{
			return (FontStyle)GetValue(FontStyleProperty);
		}
		set
		{
			SetValue(FontStyleProperty, value);
		}
	}

	public FontWeight FontWeight
	{
		get
		{
			return (FontWeight)GetValue(FontWeightProperty);
		}
		set
		{
			SetValue(FontWeightProperty, value);
		}
	}

	public FontStretch FontStretch
	{
		get
		{
			return (FontStretch)GetValue(FontStretchProperty);
		}
		set
		{
			SetValue(FontStretchProperty, value);
		}
	}

	public Brush Foreground
	{
		get
		{
			return (Brush)GetValue(ForegroundProperty);
		}
		set
		{
			SetValue(ForegroundProperty, value);
		}
	}

	public Brush Stroke
	{
		get
		{
			return (Brush)GetValue(StrokeProperty);
		}
		set
		{
			SetValue(StrokeProperty, value);
		}
	}

	public double StrokeThickness
	{
		get
		{
			return (double)GetValue(StrokeThicknessProperty);
		}
		set
		{
			SetValue(StrokeThicknessProperty, value);
		}
	}

	public TextAlignment TextAlignment
	{
		get
		{
			return (TextAlignment)GetValue(TextAlignmentProperty);
		}
		set
		{
			SetValue(TextAlignmentProperty, value);
		}
	}

	public TextWrapping TextWrapping
	{
		get
		{
			return (TextWrapping)GetValue(TextWrappingProperty);
		}
		set
		{
			SetValue(TextWrappingProperty, value);
		}
	}

	public TextTrimming TextTrimming
	{
		get
		{
			return (TextTrimming)GetValue(TextTrimmingProperty);
		}
		set
		{
			SetValue(TextTrimmingProperty, value);
		}
	}

	private double OutlineInset
	{
		get
		{
			if (!(StrokeThickness > 0.0))
			{
				return 0.0;
			}
			return StrokeThickness / 2.0 + 0.5;
		}
	}

	public OutlinedTextBlock()
	{
		base.SnapsToDevicePixels = true;
		base.UseLayoutRounding = true;
		TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
		TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
		TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		if (string.IsNullOrEmpty(Text))
		{
			return new Size(0.0, 0.0);
		}
		double inset = OutlineInset;
		double textWidth = (double.IsFinite(availableSize.Width) ? Math.Max(0.1, availableSize.Width - inset * 2.0) : 1000000.0);
		double textHeight = (double.IsFinite(availableSize.Height) ? Math.Max(0.1, availableSize.Height - inset * 2.0) : 1000000.0);
		FormattedText formattedText = CreateFormattedText(textWidth, textHeight);
		double desiredWidth = Math.Ceiling(formattedText.WidthIncludingTrailingWhitespace + inset * 2.0);
		double desiredHeight = Math.Ceiling(formattedText.Height + inset * 2.0);
		if (double.IsFinite(availableSize.Width))
		{
			desiredWidth = Math.Min(desiredWidth, availableSize.Width);
		}
		if (double.IsFinite(availableSize.Height))
		{
			desiredHeight = Math.Min(desiredHeight, availableSize.Height);
		}
		return new Size(Math.Max(0.0, desiredWidth), Math.Max(0.0, desiredHeight));
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		if (string.IsNullOrEmpty(Text) || base.ActualWidth <= 0.0 || base.ActualHeight <= 0.0)
		{
			return;
		}
		double inset = OutlineInset;
		double width = Math.Max(0.1, base.ActualWidth - inset * 2.0);
		double height = Math.Max(0.1, base.ActualHeight - inset * 2.0);
		FormattedText formatted = CreateFormattedText(width, height);
		Point origin = GetTextOrigin(formatted, inset);
		if (StrokeThickness > 0.0)
		{
			Brush stroke = Stroke;
			if (stroke != null && stroke.Opacity > 0.0)
			{
				Geometry geometry = formatted.BuildGeometry(origin);
				Pen pen = new Pen(stroke, StrokeThickness)
				{
					LineJoin = PenLineJoin.Round,
					StartLineCap = PenLineCap.Round,
					EndLineCap = PenLineCap.Round
				};
				if (pen.CanFreeze)
				{
					pen.Freeze();
				}
				drawingContext.DrawGeometry(null, pen, geometry);
			}
		}
		drawingContext.DrawText(formatted, origin);
	}

	public bool ContainsRenderedText(Point point, double tolerance = 4.0)
	{
		if (string.IsNullOrEmpty(Text) || base.ActualWidth <= 0.0 || base.ActualHeight <= 0.0 || point.X < 0.0 || point.Y < 0.0 || point.X > base.ActualWidth || point.Y > base.ActualHeight)
		{
			return false;
		}
		double inset = OutlineInset;
		FormattedText formatted = CreateFormattedText(Math.Max(0.1, base.ActualWidth - inset * 2.0), Math.Max(0.1, base.ActualHeight - inset * 2.0));
		Geometry geometry = formatted.BuildGeometry(GetTextOrigin(formatted, inset));
		if (geometry.FillContains(point))
		{
			return true;
		}
		if (tolerance <= 0.0)
		{
			return false;
		}
		Pen hitPen = new Pen(Brushes.Black, tolerance * 2.0)
		{
			LineJoin = PenLineJoin.Round,
			StartLineCap = PenLineCap.Round,
			EndLineCap = PenLineCap.Round
		};
		if (hitPen.CanFreeze)
		{
			hitPen.Freeze();
		}
		return geometry.StrokeContains(hitPen, point);
	}

	private Point GetTextOrigin(FormattedText formatted, double inset)
	{
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		return new Point(RoundToPixel(inset, dpi.DpiScaleX), RoundToPixel(Math.Max(inset, (base.ActualHeight - formatted.Height) / 2.0), dpi.DpiScaleY));
	}

	private FormattedText CreateFormattedText(double maxWidth, double maxHeight)
	{
		DpiScale dpi = VisualTreeHelper.GetDpi(this);
		Typeface typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
		FormattedText formatted = new FormattedText(Text ?? string.Empty, CultureInfo.CurrentUICulture, base.FlowDirection, typeface, FontSize, Foreground, null, TextFormattingMode.Display, dpi.PixelsPerDip)
		{
			TextAlignment = TextAlignment,
			Trimming = TextTrimming,
			MaxTextWidth = Math.Max(0.1, maxWidth),
			MaxTextHeight = Math.Max(0.1, maxHeight)
		};
		if (TextWrapping == TextWrapping.NoWrap)
		{
			formatted.MaxLineCount = 1;
		}
		return formatted;
	}

	private static double RoundToPixel(double value, double scale)
	{
		if (!(scale > 0.0))
		{
			return value;
		}
		return Math.Round(value * scale) / scale;
	}
}
