using System;
using System.Numerics;
using System.Diagnostics;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Foundation.Metadata;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Unison.UWPApp.UI.Controls
{
    public sealed partial class MessageBubbleChrome : UserControl
    {
        private static readonly int[] SupportedScales = new[] { 100, 120, 125, 150, 160, 200, 225, 250, 300, 400, 500 };

        private SpriteVisual _bubbleVisual;
        private bool _compositionReady;
        private bool _compositionAttempted;
        private bool _isLoaded;
        private string _lastKey;

        public MessageBubbleChrome()
        {
            this.InitializeComponent();
            Loaded += MessageBubbleChrome_Loaded;
            Unloaded += MessageBubbleChrome_Unloaded;
            SizeChanged += MessageBubbleChrome_SizeChanged;
            UpdateFallbackVisual();
        }

        public static readonly DependencyProperty IsFromMeProperty =
            DependencyProperty.Register(nameof(IsFromMe), typeof(bool), typeof(MessageBubbleChrome), new PropertyMetadata(false, OnBubblePropertyChanged));

        public static readonly DependencyProperty IsRunStartProperty =
            DependencyProperty.Register(nameof(IsRunStart), typeof(bool), typeof(MessageBubbleChrome), new PropertyMetadata(true, OnBubblePropertyChanged));

        public static readonly DependencyProperty ThemeModeProperty =
            DependencyProperty.Register(nameof(ThemeMode), typeof(string), typeof(MessageBubbleChrome), new PropertyMetadata("Dark", OnBubblePropertyChanged));

        public static readonly DependencyProperty BubbleSizeProperty =
            DependencyProperty.Register(nameof(BubbleSize), typeof(Size), typeof(MessageBubbleChrome), new PropertyMetadata(default(Size)));

        public static readonly DependencyProperty ForceFallbackProperty =
            DependencyProperty.Register(nameof(ForceFallback), typeof(bool), typeof(MessageBubbleChrome), new PropertyMetadata(false, OnBubblePropertyChanged));

        public bool IsFromMe
        {
            get { return (bool)GetValue(IsFromMeProperty); }
            set { SetValue(IsFromMeProperty, value); }
        }

        public bool IsRunStart
        {
            get { return (bool)GetValue(IsRunStartProperty); }
            set { SetValue(IsRunStartProperty, value); }
        }

        public string ThemeMode
        {
            get { return (string)GetValue(ThemeModeProperty); }
            set { SetValue(ThemeModeProperty, value); }
        }

        public Size BubbleSize
        {
            get { return (Size)GetValue(BubbleSizeProperty); }
            set { SetValue(BubbleSizeProperty, value); }
        }

        public bool ForceFallback
        {
            get { return (bool)GetValue(ForceFallbackProperty); }
            set { SetValue(ForceFallbackProperty, value); }
        }

        private static void OnBubblePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as MessageBubbleChrome;
            if (control == null)
            {
                return;
            }

            control.UpdateFallbackVisual();
            control.TryConfigureComposition();
        }

        private void MessageBubbleChrome_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            TryConfigureComposition();
            UpdateVisualSize();
        }

        private void MessageBubbleChrome_Unloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            if (_bubbleVisual != null)
            {
                ElementCompositionPreview.SetElementChildVisual(CompositionHost, null);
                _bubbleVisual = null;
            }

            _compositionReady = false;
            _compositionAttempted = false;
        }

        private void MessageBubbleChrome_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            BubbleSize = e.NewSize;
            UpdateVisualSize();
        }

        private void UpdateVisualSize()
        {
            if (_bubbleVisual == null)
            {
                return;
            }

            float width = (float)Math.Max(0.0, ActualWidth);
            float height = (float)Math.Max(0.0, ActualHeight);
            _bubbleVisual.Size = new Vector2(width, height);
        }

        private void TryConfigureComposition()
        {
            if (!_isLoaded || ForceFallback)
            {
                _compositionReady = false;
                FallbackHost.Visibility = Visibility.Visible;
                if (_bubbleVisual != null)
                {
                    ElementCompositionPreview.SetElementChildVisual(CompositionHost, null);
                    _bubbleVisual = null;
                }
                return;
            }

            if (!_compositionAttempted)
            {
                _compositionAttempted = true;
                _compositionReady = CanUseCompositionBubbles();
            }

            if (!_compositionReady)
            {
                FallbackHost.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                var compositor = ElementCompositionPreview.GetElementVisual(CompositionHost).Compositor;
                if (_bubbleVisual == null)
                {
                    _bubbleVisual = compositor.CreateSpriteVisual();
                    ElementCompositionPreview.SetElementChildVisual(CompositionHost, _bubbleVisual);
                }

                string variant = IsFromMe ? "outgoing" : "incoming";
                if (IsRunStart)
                {
                    variant += " first";
                }

                int scale = GetNearestScale();
                string darkUri = BuildAssetUri("dark", variant, scale);
                string maskUri = BuildAssetUri("mask", variant, scale);
                string key = variant + "|" + scale;
                if (!string.Equals(_lastKey, key, StringComparison.Ordinal))
                {
                    _lastKey = key;
                    Debug.WriteLine("[MessageBubbleChrome] variant=" + variant + " scale=" + scale);
                }

                var darkSurface = LoadedImageSurface.StartLoadFromUri(new Uri(darkUri));
                var darkSurfaceBrush = compositor.CreateSurfaceBrush(darkSurface);
                darkSurfaceBrush.Stretch = CompositionStretch.None;

                var darkNineGrid = compositor.CreateNineGridBrush();
                darkNineGrid.Source = darkSurfaceBrush;
                ConfigureInsets(darkNineGrid, IsFromMe, IsRunStart, scale);

                var maskSurface = LoadedImageSurface.StartLoadFromUri(new Uri(maskUri));
                var maskSurfaceBrush = compositor.CreateSurfaceBrush(maskSurface);
                maskSurfaceBrush.Stretch = CompositionStretch.None;

                var maskNineGrid = compositor.CreateNineGridBrush();
                maskNineGrid.Source = maskSurfaceBrush;
                ConfigureInsets(maskNineGrid, IsFromMe, IsRunStart, scale);

                var maskBrush = compositor.CreateMaskBrush();
                maskBrush.Source = darkNineGrid;
                maskBrush.Mask = maskNineGrid;

                _bubbleVisual.Brush = maskBrush;
                FallbackHost.Visibility = Visibility.Collapsed;
                UpdateVisualSize();
            }
            catch
            {
                _compositionReady = false;
                FallbackHost.Visibility = Visibility.Visible;
                if (_bubbleVisual != null)
                {
                    ElementCompositionPreview.SetElementChildVisual(CompositionHost, null);
                    _bubbleVisual = null;
                }
            }
        }

        private static void ConfigureInsets(CompositionNineGridBrush brush, bool isFromMe, bool isRunStart, int scale)
        {
            float factor = scale / 100f;
            float left = 8f;
            float top = 6f;
            float right = 8f;
            float bottom = 8f;

            if (isRunStart)
            {
                if (isFromMe)
                {
                    right = 12f;
                }
                else
                {
                    left = 12f;
                }
            }

            brush.LeftInset = left * factor;
            brush.TopInset = top * factor;
            brush.RightInset = right * factor;
            brush.BottomInset = bottom * factor;
        }

        private static bool CanUseCompositionBubbles()
        {
            return
                ApiInformation.IsTypePresent("Windows.UI.Composition.CompositionNineGridBrush") &&
                ApiInformation.IsTypePresent("Windows.UI.Composition.LoadedImageSurface") &&
                ApiInformation.IsMethodPresent("Windows.UI.Composition.Compositor", "CreateMaskBrush") &&
                ApiInformation.IsMethodPresent("Windows.UI.Xaml.Hosting.ElementCompositionPreview", "SetElementChildVisual");
        }

        private static string BuildAssetUri(string family, string variant, int scale)
        {
            string escapedVariant = Uri.EscapeDataString(variant);
            return "ms-appx:///Assets/Bubbles/" + family + "/" + escapedVariant + ".scale-" + scale + ".png";
        }

        private static int GetNearestScale()
        {
            int currentScale;
            try
            {
                currentScale = (int)DisplayInformation.GetForCurrentView().ResolutionScale;
            }
            catch
            {
                currentScale = 100;
            }

            int best = SupportedScales[0];
            double bestDiff = Math.Abs(best - currentScale);
            for (int i = 1; i < SupportedScales.Length; i++)
            {
                int candidate = SupportedScales[i];
                double diff = Math.Abs(candidate - currentScale);
                if (diff < bestDiff)
                {
                    best = candidate;
                    bestDiff = diff;
                }
            }

            return best;
        }

        private void UpdateFallbackVisual()
        {
            if (!ForceFallback && _compositionReady)
            {
                return;
            }

            bool isFromMe = IsFromMe;
            bool showTail = IsRunStart;

            var highlightColor = isFromMe ? Color.FromArgb(255, 16, 102, 86) : Color.FromArgb(255, 62, 62, 62);
            var fillColor = isFromMe ? Color.FromArgb(255, 0, 92, 75) : Color.FromArgb(255, 54, 54, 54);

            BodyHighlightBorder.Background = new SolidColorBrush(highlightColor);
            BodyFillBorder.Background = new SolidColorBrush(fillColor);
            BodyHighlightBorder.CornerRadius = isFromMe
                ? new CornerRadius(16, 16, showTail ? 4 : 16, 16)
                : new CornerRadius(16, 16, 16, showTail ? 4 : 16);
            BodyFillBorder.CornerRadius = BodyHighlightBorder.CornerRadius;

            TailContainer.Visibility = showTail ? Visibility.Visible : Visibility.Collapsed;
            TailHighlightPath.Fill = new SolidColorBrush(highlightColor);
            TailFillPath.Fill = new SolidColorBrush(fillColor);
            TailHighlightPath.Data = BuildTailGeometry();
            TailFillPath.Data = BuildTailGeometry();

            if (isFromMe)
            {
                TailContainer.HorizontalAlignment = HorizontalAlignment.Right;
                TailContainer.RenderTransform = null;
                TailContainer.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            else
            {
                TailContainer.HorizontalAlignment = HorizontalAlignment.Left;
                TailContainer.RenderTransform = new ScaleTransform { ScaleX = -1, ScaleY = 1 };
                TailContainer.RenderTransformOrigin = new Point(0.5, 0.5);
            }
        }

        private static Geometry BuildTailGeometry()
        {
            var figure = new PathFigure
            {
                StartPoint = new Point(0, 0),
                IsClosed = true,
                IsFilled = true
            };
            figure.Segments.Add(new LineSegment { Point = new Point(10, 0) });
            figure.Segments.Add(new LineSegment { Point = new Point(0, 10) });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }
    }
}
