using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CaptionClock;

/// <summary>Draws subtitle text with a configurable stroke so it remains legible over video.</summary>
public sealed class OutlinedCaptionText : FrameworkElement
{
    private string _text = string.Empty;
    private FontFamily _fontFamily = new("Segoe UI");
    private double _fontSize = 42;
    private FontWeight _fontWeight = FontWeights.Normal;
    private FontStyle _fontStyle = FontStyles.Normal;
    private TextAlignment _textAlignment = TextAlignment.Center;
    private bool _underline;
    private Brush _foreground = Brushes.White;
    private Brush _outlineBrush = Brushes.Black;
    private double _outlineThickness = 2.5;

    public string Text { get => _text; set { _text = value ?? string.Empty; Refresh(); } }
    public FontFamily FontFamily { get => _fontFamily; set { _fontFamily = value ?? new FontFamily("Segoe UI"); Refresh(); } }
    public double FontSize { get => _fontSize; set { _fontSize = Math.Max(1, value); Refresh(); } }
    public FontWeight FontWeight { get => _fontWeight; set { _fontWeight = value; Refresh(); } }
    public FontStyle FontStyle { get => _fontStyle; set { _fontStyle = value; Refresh(); } }
    public TextAlignment TextAlignment { get => _textAlignment; set { _textAlignment = value; Refresh(); } }
    public bool Underline { get => _underline; set { _underline = value; Refresh(); } }
    public Brush Foreground { get => _foreground; set { _foreground = value ?? Brushes.White; Refresh(); } }
    public Brush OutlineBrush { get => _outlineBrush; set { _outlineBrush = value ?? Brushes.Black; Refresh(); } }
    public double OutlineThickness { get => _outlineThickness; set { _outlineThickness = Math.Max(0, value); Refresh(); } }

    private void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private FormattedText CreateFormattedText(double availableWidth)
    {
        var padding = OutlineThickness + 2;
        var width = double.IsInfinity(availableWidth) ? 1200 : Math.Max(1, availableWidth - padding * 2);
        var formatted = new FormattedText(
            Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretches.Normal),
            FontSize,
            Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = width,
            TextAlignment = TextAlignment
        };
        if (Underline) formatted.SetTextDecorations(TextDecorations.Underline);
        return formatted;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var formatted = CreateFormattedText(availableSize.Width);
        var padding = OutlineThickness + 2;
        return new Size(
            Math.Min(availableSize.Width, Math.Max(1, formatted.WidthIncludingTrailingWhitespace + padding * 2)),
            Math.Max(1, formatted.Height + padding * 2));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (string.IsNullOrEmpty(Text) || ActualWidth <= 0 || ActualHeight <= 0) return;

        var padding = OutlineThickness + 2;
        var formatted = CreateFormattedText(ActualWidth);
        var geometry = formatted.BuildGeometry(new Point(padding, padding));
        if (OutlineThickness > 0)
        {
            var pen = new Pen(OutlineBrush, OutlineThickness) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }
        drawingContext.DrawGeometry(Foreground, null, geometry);
    }
}
