using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.Foundation;

namespace Unison.UWPApp.UI.Controls
{
    public sealed partial class ModernProgressRing : UserControl
    {
        private bool _isActive;
        private long _startTime;
        private readonly TimeSpan _duration = TimeSpan.FromSeconds(2);

        public ModernProgressRing()
        {
            this.InitializeComponent();
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
            this.SizeChanged += OnSizeChanged;
        }

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register("IsActive", typeof(bool), typeof(ModernProgressRing), new PropertyMetadata(true, OnIsActiveChanged));

        public bool IsActive
        {
            get { return (bool)GetValue(IsActiveProperty); }
            set { SetValue(IsActiveProperty, value); }
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (ModernProgressRing)d;
            control.Visibility = control.IsActive ? Visibility.Visible : Visibility.Collapsed;
            control.UpdateAnimationState();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateAnimationState();
            UpdatePathLayout();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopAnimation();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePathLayout();
        }

        private void UpdateAnimationState()
        {
            if (IsActive && this.Visibility == Visibility.Visible)
            {
                StartAnimation();
            }
            else
            {
                StopAnimation();
            }
        }

        private void StartAnimation()
        {
            if (!_isActive)
            {
                _isActive = true;
                _startTime = DateTime.Now.Ticks;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private void StopAnimation()
        {
            if (_isActive)
            {
                _isActive = false;
                CompositionTarget.Rendering -= OnRendering;
            }
        }

        private void OnRendering(object sender, object e)
        {
            if (!_isActive) return;

            long currentTime = DateTime.Now.Ticks;
            double seconds = TimeSpan.FromTicks(currentTime - _startTime).TotalSeconds;
            double t = (seconds % 2.0) / 2.0; // Normalized time 0..1

            // Ensure components are initialized (safety for Designer)
            if (SpinnerTransform == null) return;

            // 1. Global Rotation: -90 to 810 degrees (total 900 degrees rotation over 2s)
            // The SVG says values="-90;810".
            double rotation = -90 + (900 * t);
            SpinnerTransform.Rotation = rotation;

            // 2. Arc Length Animation
            // Head (EndAngle) and Tail (StartAngle).
            
            // Simplified "worm" model
            
            double startAngle = 0;
            double endAngle = 0;
            
            if (t < 0.5)
            {
                // 0 to 0.5
                double localT = t * 2; // 0 to 1
                localT = EaseOutCubic(localT); // Easing
                
                startAngle = 0;
                endAngle = 180 * localT;
            }
            else
            {
                // 0.5 to 1.0
                double localT = (t - 0.5) * 2; // 0 to 1
                localT = EaseOutCubic(localT); // Easing
                
                startAngle = 180 * localT;
                endAngle = 180;
            }
            
            // Update Arc
            UpdateArc(startAngle, endAngle);
        }

        private double EaseOutCubic(double t)
        {
            return 1 - Math.Pow(1 - t, 3);
        }

        private void UpdateArc(double startAngle, double endAngle)
        {
            if (SpinnerFigure == null) return;

            // Ensure angles are valid
            if (Math.Abs(endAngle - startAngle) < 0.1)
            {
                SpinnerFigure.StartPoint = new Point(0, 0); 
            }
            
            if (SpinnerPath == null || SpinnerFigure == null || SpinnerArc == null) return;

            // Scale thickness proportional to size
            double size = Math.Min(this.ActualWidth, this.ActualHeight);
            if (size <= 0) return;
            
            double thickness = size * 0.1; // 10% thickness
            SpinnerPath.StrokeThickness = thickness;

            // Convert polar to cartesian
            // Center is (Width/2, Height/2). Radius is (Width/2 - Thickness/2).
            double radius = (size - thickness) / 2;
            double cx = size / 2;
            double cy = size / 2;

            // Angles are in degrees.
            
            // Convert to radians
            double startRad = (startAngle - 90) * Math.PI / 180;
            double endRad = (endAngle - 90) * Math.PI / 180;

            Point startPoint = new Point(
                cx + radius * Math.Cos(startRad),
                cy + radius * Math.Sin(startRad));

            Point endPoint = new Point(
                cx + radius * Math.Cos(endRad),
                cy + radius * Math.Sin(endRad));

            SpinnerFigure.StartPoint = startPoint;
            SpinnerArc.Point = endPoint;
            SpinnerArc.IsLargeArc = (endAngle - startAngle) > 180;
            SpinnerArc.Size = new Size(radius, radius);
        }

        private void UpdatePathLayout()
        {
            if (SpinnerPath == null) return;
            SpinnerPath.Width = this.ActualWidth;
            SpinnerPath.Height = this.ActualHeight;
        }
    }
}
