using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BatteryBar;

/// <summary>
/// Плавная прокрутка колесом, как в «Параметрах» Windows: колесо сдвигает цель, а содержимое
/// догоняет её за несколько кадров с замедлением. Включается в XAML: local:SmoothScroll.IsEnabled="True".
/// </summary>
internal static class SmoothScroll
{
    // Пикселей на один щелчок колеса (Delta = 120); стандартный ScrollViewer сдвигает на 48.
    private const double PixelsPerNotch = 100;

    // Скорость догоняния: за 1/60 с проходим около 18% оставшегося пути — движение затухает примерно за 0,25 с.
    private const double Speed = 12;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Animator), typeof(SmoothScroll));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer || e.NewValue is not true) return;
        var animator = new Animator(viewer);
        viewer.SetValue(StateProperty, animator);
        viewer.PreviewMouseWheel += animator.OnWheel;
    }

    private sealed class Animator
    {
        private readonly ScrollViewer _viewer;
        private double _target;
        private TimeSpan _lastFrame;
        private bool _running;

        public Animator(ScrollViewer viewer) => _viewer = viewer;

        public void OnWheel(object sender, MouseWheelEventArgs e)
        {
            // Колесо над вложенным списком с собственной прокруткой оставляем ему.
            if (e.OriginalSource is DependencyObject source && InnerScrollable(source)) return;
            if (_viewer.ScrollableHeight <= 0) return;
            e.Handled = true;

            // Новая прокрутка после паузы (или после перетаскивания полосы) начинается с текущего места.
            if (!_running) _target = _viewer.VerticalOffset;
            _target = Math.Clamp(_target - e.Delta / 120.0 * PixelsPerNotch, 0, _viewer.ScrollableHeight);
            if (_running) return;
            _running = true;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _lastFrame).TotalSeconds, 0.1);
            if (dt <= 0) return; // один и тот же кадр может прийти дважды
            _lastFrame = now;

            double offset = _viewer.VerticalOffset;
            double distance = _target - offset;
            if (Math.Abs(distance) < 0.5)
            {
                _viewer.ScrollToVerticalOffset(_target);
                CompositionTarget.Rendering -= OnFrame;
                _running = false;
                return;
            }
            // Доля пути за кадр не зависит от частоты обновления экрана (60, 144 Гц).
            _viewer.ScrollToVerticalOffset(offset + distance * (1 - Math.Exp(-Speed * dt)));
        }

        private bool InnerScrollable(DependencyObject source)
        {
            for (var d = source; d != null && d != _viewer; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            {
                if (d is ScrollViewer inner && inner.ScrollableHeight > 0) return true;
                if (d is TextBox { AcceptsReturn: true }) return true;
            }
            return false;
        }
    }
}
