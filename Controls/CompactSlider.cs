using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

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

    public static readonly StyledProperty<int> UpdateFrequencyMsProperty =
        AvaloniaProperty.Register<CompactSlider, int>(
            nameof(UpdateFrequencyMs),
            defaultValue: 50);

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

    public int UpdateFrequencyMs
    {
        get => GetValue(UpdateFrequencyMsProperty);
        set => SetValue(UpdateFrequencyMsProperty, value);
    }

    static CompactSlider()
    {
        BackgroundProperty.OverrideDefaultValue<CompactSlider>(Brushes.Transparent);
    }

    private bool _isDragging;
    private bool _isUpdatingFromDrag;
    private double _dragX;
    private double _pointerOffsetInsideThumb;
    private double _activeMaximum;
    private double? _pendingMaximum;
    private readonly DispatcherTimer _throttleTimer;

    public CompactSlider()
    {
        _throttleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(UpdateFrequencyMs)
        };
        _throttleTimer.Tick += OnThrottleTimerTick;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
        {
            if (!_isDragging && !_isUpdatingFromDrag)
            {
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
            }
        }
        else if (change.Property == MaximumProperty)
        {
            double newMax = change.GetNewValue<double>();

            if (_isDragging)
            {
                _pendingMaximum = newMax;
            }
            else
            {
                _activeMaximum = newMax;
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        Point pos = e.GetPosition(this);
        PointerPoint pointDetails = e.GetCurrentPoint(this);

        if (pointDetails.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _activeMaximum = Maximum;
            _pendingMaximum = null;

            double thumbWidth = 22.0;
            double usableWidth = Math.Max(0, Bounds.Width - thumbWidth);

            double activeMax = _activeMaximum > 0 ? _activeMaximum : 1.0;
            double pct = Math.Clamp(Value / activeMax, 0.0, 1.0);
            double currentThumbX = usableWidth * (1.0 - pct);

            if (pos.X >= currentThumbX && pos.X <= currentThumbX + thumbWidth)
            {
                _pointerOffsetInsideThumb = pos.X - currentThumbX;
            }
            else
            {
                _pointerOffsetInsideThumb = thumbWidth / 2.0;
            }

            e.Pointer.Capture(this);
            UpdateDragPosition(pos.X);
            _throttleTimer.Start();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_isDragging)
        {
            Point pos = e.GetPosition(this);
            UpdateDragPosition(pos.X);
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
            _throttleTimer.Stop();

            CommitValueFromDragX();

            if (_pendingMaximum.HasValue)
            {
                _activeMaximum = _pendingMaximum.Value;
                _pendingMaximum = null;
            }

            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        _isDragging = false;
        _throttleTimer.Stop();

        if (_pendingMaximum.HasValue)
        {
            _activeMaximum = _pendingMaximum.Value;
            _pendingMaximum = null;
        }

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    private void UpdateDragPosition(double pointerX)
    {
        double thumbWidth = 22.0;
        double usableWidth = Bounds.Width - thumbWidth;

        if (usableWidth <= 0) return;

        double newDragX = Math.Clamp(pointerX - _pointerOffsetInsideThumb, 0.0, usableWidth);

        // Only schedule a render pass if the position actually shifted by a subpixel threshold
        if (Math.Abs(newDragX - _dragX) > 0.1)
        {
            _dragX = newDragX;
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
    }

    private void OnThrottleTimerTick(object? sender, EventArgs e)
    {
        if (_isDragging)
        {
            CommitValueFromDragX();
        }
    }

    private void CommitValueFromDragX()
    {
        double thumbWidth = 22.0;
        double usableWidth = Bounds.Width - thumbWidth;

        if (usableWidth <= 0 || _activeMaximum <= 0) return;

        double percent = 1.0 - (_dragX / usableWidth);
        double newValue = percent * _activeMaximum;

        _isUpdatingFromDrag = true;
        try
        {
            Value = newValue;
        }
        finally
        {
            _isUpdatingFromDrag = false;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Background != null)
        {
            context.FillRectangle(Background, new Rect(Bounds.Size));
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        double midY = Bounds.Height / 2.0;

        // 2px Track Line
        context.DrawLine(new Pen(Brushes.Gray, 2), new Point(0, midY), new Point(Bounds.Width, midY));

        DrawThumb(context, midY);
    }

    private void DrawThumb(DrawingContext context, double midY)
    {
        double thumbWidth = 22.0;
        double thumbHeight = 14.0;
        double thumbX;

        if (_isDragging)
        {
            thumbX = _dragX;
        }
        else
        {
            double activeMax = _activeMaximum > 0 ? _activeMaximum : Maximum;
            double pct = activeMax > 0 ? Math.Clamp(Value / activeMax, 0.0, 1.0) : 0;
            thumbX = (Bounds.Width - thumbWidth) * (1.0 - pct);
        }

        double thumbY = midY - (thumbHeight / 2.0);
        var thumbRect = new Rect(thumbX, thumbY, thumbWidth, thumbHeight);

        context.DrawRectangle(Brushes.DimGray, new Pen(Brushes.DarkGray, 1), new RoundedRect(thumbRect, 3, 3));

        // Draw "<" and ">" vector symbols
        var pen = new Pen(Brushes.White, 1.5);
        double centerX = thumbRect.X + thumbRect.Width / 2.0;
        double centerY = thumbRect.Y + thumbRect.Height / 2.0;

        // Left arrow "<"
        context.DrawLine(pen, new Point(centerX - 2, centerY - 3), new Point(centerX - 5, centerY));
        context.DrawLine(pen, new Point(centerX - 5, centerY), new Point(centerX - 2, centerY + 3));

        // Right arrow ">"
        context.DrawLine(pen, new Point(centerX + 2, centerY - 3), new Point(centerX + 5, centerY));
        context.DrawLine(pen, new Point(centerX + 5, centerY), new Point(centerX + 2, centerY + 3));
    }
}