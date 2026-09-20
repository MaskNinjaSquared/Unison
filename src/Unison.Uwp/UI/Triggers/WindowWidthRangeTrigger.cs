using System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Unison.Uwp.UI.Triggers
{
    /// <summary>
    /// Activates when <see cref="Window.Current"/> width is in
    /// [<see cref="MinWindowWidth"/>, <see cref="MaxWindowWidth"/>).
    /// Prefer this inside ContentDialog — AdaptiveTrigger often does not refresh in Popup.
    /// Do not call <see cref="StateTriggerBase.SetActive"/> from the constructor (UWP ignores it).
    /// </summary>
    public sealed class WindowWidthRangeTrigger : StateTriggerBase
    {
        public static readonly DependencyProperty MinWindowWidthProperty =
            DependencyProperty.Register(
                nameof(MinWindowWidth),
                typeof(double),
                typeof(WindowWidthRangeTrigger),
                new PropertyMetadata(0d, OnRangeChanged));

        public static readonly DependencyProperty MaxWindowWidthProperty =
            DependencyProperty.Register(
                nameof(MaxWindowWidth),
                typeof(double),
                typeof(WindowWidthRangeTrigger),
                new PropertyMetadata(double.PositiveInfinity, OnRangeChanged));

        private bool _listening;

        public WindowWidthRangeTrigger()
        {
            // Defer: SetActive in ctor is ignored before the trigger is in a VisualState.
            var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                EnsureListening();
                UpdateActive();
            });
        }

        public double MinWindowWidth
        {
            get { return (double)GetValue(MinWindowWidthProperty); }
            set { SetValue(MinWindowWidthProperty, value); }
        }

        /// <summary>Exclusive upper bound (default +∞).</summary>
        public double MaxWindowWidth
        {
            get { return (double)GetValue(MaxWindowWidthProperty); }
            set { SetValue(MaxWindowWidthProperty, value); }
        }

        private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var trigger = d as WindowWidthRangeTrigger;
            if (trigger == null)
            {
                return;
            }

            trigger.EnsureListening();
            trigger.UpdateActive();
        }

        private void EnsureListening()
        {
            if (_listening)
            {
                return;
            }

            try
            {
                Window.Current.SizeChanged += Window_SizeChanged;
                _listening = true;
            }
            catch
            {
            }
        }

        private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            UpdateActive();
        }

        private void UpdateActive()
        {
            double width;
            try
            {
                width = Window.Current.Bounds.Width;
            }
            catch
            {
                SetActive(false);
                return;
            }

            double min = MinWindowWidth;
            double max = MaxWindowWidth;
            if (double.IsNaN(max) || double.IsInfinity(max))
            {
                max = double.PositiveInfinity;
            }

            SetActive(width >= min && width < max);
        }
    }
}
