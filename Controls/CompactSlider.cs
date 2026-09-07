using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace MultiPing.Controls;

public class CompactSlider : TemplatedControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<CompactSlider, double>(
            nameof(Value),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<CompactSlider, double>(
            nameof(Maximum),
            defaultValue: 100.0);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    static CompactSlider()
    {
        // Force redraw on property updates
        AffectsRender<CompactSlider>(ValueProperty, MaximumProperty, BackgroundProperty);
        
        // Ensure default background is hit-testable
        BackgroundProperty.OverrideDefaultValue<CompactSlider>(Brushes.Transparent);
    }

    private bool _isDragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        Point pos = e.GetPosition(this);
        PointerPoint pointDetails = e.GetCurrentPoint(this);

        if (pointDetails.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            e.Pointer.Capture(this);

            UpdateValueFromPointer(pos);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_isDragging)
        {
            Point pos = e.GetPosition(this);
            UpdateValueFromPointer(pos);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isDragging = false;
    }

    private void UpdateValueFromPointer(Point pos)
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        if (Maximum <= 0)
        {
            return;
        }

        double clampX = Math.Clamp(pos.X, 0.0, Bounds.Width);
        // Right-to-Left: 0 on right edge, Maximum on left edge
        double percent = 1.0 - (clampX / Bounds.Width);
        double newValue = percent * Maximum;

        Value = newValue;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 1. Draw hit-testable background
        if (Background != null)
        {
            context.FillRectangle(Background, new Rect(Bounds.Size));
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        double midY = Bounds.Height / 2.0;

        // 2. Draw 2px Track Line
        context.DrawLine(new Pen(Brushes.Gray, 2), new Point(0, midY), new Point(Bounds.Width, midY));

        // 3. Draw 8px Circle Thumb (Right-to-Left placement)
        double pct = Maximum > 0 ? Math.Clamp(Value / Maximum, 0.0, 1.0) : 0;
        double thumbX = Bounds.Width * (1.0 - pct);

        context.DrawEllipse(Brushes.DimGray, new Pen(Brushes.DarkGray, 1), new Point(thumbX, midY), 4, 4);
    }
}