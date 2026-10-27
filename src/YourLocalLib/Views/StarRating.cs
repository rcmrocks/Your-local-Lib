using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Windows.Input;

namespace YourLocalLib.Views;

public sealed class StarRating : Control
{
    public static readonly StyledProperty<double?> RatingProperty =
        AvaloniaProperty.Register<StarRating, double?>(nameof(Rating));

    public static readonly StyledProperty<ICommand?> SetRatingCommandProperty =
        AvaloniaProperty.Register<StarRating, ICommand?>(nameof(SetRatingCommand));

    private const double StarSize = 32;
    private const double Gap = 8;
    private const double Stride = StarSize + Gap;
    private const int StarCount = 5;
    private const double StarsWidth = StarCount * StarSize + (StarCount - 1) * Gap;

    private static readonly Geometry StarShape = Geometry.Parse(
        "M12,1.6 L14.9,8.5 22,9.2 16.8,14 18.4,21.2 12,17.3 5.6,21.2 7.2,14 2,9.2 9.1,8.5 Z");

    private double? _hover;
    private IPointer? _capture;

    public StarRating()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
        ToolTip.SetTip(this, "Move across a star for a half step. Click to keep that rating.");
    }

    public double? Rating
    {
        get => GetValue(RatingProperty);
        set => SetValue(RatingProperty, value);
    }

    public ICommand? SetRatingCommand
    {
        get => GetValue(SetRatingCommandProperty);
        set => SetValue(SetRatingCommandProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return new Size(StarsWidth + 112, 36);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RatingProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var hovering = _hover != null;
        var shown = _hover ?? Rating ?? 0;
        var fill = FindBrush("AccentBrush", "#1E4D3A");
        var empty = FindBrush("HairlineBrush", "#DDD4C6");
        var ink = FindBrush("InkBrush", "#1E1A16");
        var muted = FindBrush("MutedBrush", "#6B6258");
        for (var i = 0; i < StarCount; i++)
        {
            var fraction = Math.Clamp(shown - i, 0, 1);
            using (context.PushTransform(Matrix.CreateTranslation(i * Stride, 2)))
            using (context.PushTransform(Matrix.CreateScale(StarSize / 24.0, StarSize / 24.0)))
            {
                context.DrawGeometry(empty, null, StarShape);
                if (fraction > 0)
                {
                    using (context.PushClip(new Rect(0, 0, 24 * fraction, 24)))
                        context.DrawGeometry(fill, null, StarShape);
                }
            }
        }

        var caption = !hovering && Rating is null
            ? "Not rated"
            : shown.ToString("0.#", CultureInfo.InvariantCulture);
        var brush = hovering ? fill : Rating is null ? muted : ink;
        var text = new FormattedText(caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI, Inter")), 15, brush);
        var y = Math.Max(0, (Bounds.Height - text.Height) / 2);
        context.DrawText(text, new Point(StarsWidth + 12, y));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHover(ValueAt(e.GetPosition(this).X));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _capture = e.Pointer;
            e.Pointer.Capture(this);
            SetHover(ValueAt(e.GetPosition(this).X));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        var value = ValueAt(e.GetPosition(this).X);
        _capture = null;
        e.Pointer.Capture(null);
        if (value is double chosen && SetRatingCommand?.CanExecute(chosen) == true)
            SetRatingCommand.Execute(chosen);
        SetHover(Bounds.Contains(e.GetPosition(this)) ? value : null);
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_capture != null)
            return;
        SetHover(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _capture = null;
        SetHover(null);
    }

    private void SetHover(double? value)
    {
        if (_hover == value)
            return;
        _hover = value;
        InvalidateVisual();
    }

    private static double? ValueAt(double x)
    {
        if (x < 0 || x > StarsWidth)
            return null;
        var index = (int)(x / Stride);
        if (index >= StarCount)
            index = StarCount - 1;
        var local = x - index * Stride;
        if (local >= StarSize)
            return index + 1;
        return index + (local < StarSize / 2 ? 0.5 : 1);
    }

    private IBrush FindBrush(string key, string fallback)
    {
        if (this.TryFindResource(key, out var found) && found is IBrush brush)
            return brush;
        return new SolidColorBrush(Color.Parse(fallback));
    }
}
