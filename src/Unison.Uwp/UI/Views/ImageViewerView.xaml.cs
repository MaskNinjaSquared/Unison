using System;
using System.ComponentModel;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Full-screen image overlay. Pinch/pan via CompositeTransform
    /// (ported from Imgur FullScreenImageView). Top/bottom chrome overlays the image
    /// without resizing it when toggled.
    /// </summary>
    public sealed partial class ImageViewerView : UserControl
    {
        private const double MinZoom = 1.0;
        private const double MaxZoom = 5.0;
        private const double WheelZoomStep = 1.12;
        private static readonly object ClosedDataContext = new object();

        private ImageViewerViewModel _viewModel;
        private int _naturalWidth;
        private int _naturalHeight;
        private double _displayWidth;
        private double _displayHeight;
        private double _zoom = MinZoom;

        public ImageViewerView()
        {
            InitializeComponent();
            DataContext = ClosedDataContext;
            Unloaded += ImageViewerView_Unloaded;
            SizeChanged += ImageViewerView_SizeChanged;
            ImageArea.SizeChanged += ImageArea_SizeChanged;
        }

        public ImageViewerViewModel ViewModel
        {
            get => _viewModel;
            set
            {
                if (_viewModel != null)
                {
                    _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                    _viewModel.CloseRequested -= ViewModel_CloseRequested;
                }

                _viewModel = value;
                // Avoid inheriting ChatDetailViewModel while overlay is closed (Binding path spam / wrong VM).
                DataContext = (object)value ?? ClosedDataContext;
                Bindings.Update();
                _naturalWidth = 0;
                _naturalHeight = 0;
                _displayWidth = 0;
                _displayHeight = 0;

                if (_viewModel != null)
                {
                    _viewModel.PropertyChanged += ViewModel_PropertyChanged;
                    _viewModel.CloseRequested += ViewModel_CloseRequested;
                    ApplyChromeVisibility(_viewModel.IsChromeVisible, animate: false);
                    LoadImageSource(_viewModel.ImageUri);
                    ResetZoom();
                }
                else
                {
                    ViewerImage.Source = null;
                }
            }
        }

        public event EventHandler CloseRequested;

        private void ImageViewerView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.CloseRequested -= ViewModel_CloseRequested;
            }
        }

        private void ImageViewerView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateImageLayout();
        }

        private void ImageArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateImageLayout();
        }

        private void ViewModel_CloseRequested(object sender, EventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ImageViewerViewModel.IsChromeVisible) && _viewModel != null)
            {
                ApplyChromeVisibility(_viewModel.IsChromeVisible, animate: true);
            }
            else if (e.PropertyName == nameof(ImageViewerViewModel.ImageUri))
            {
                LoadImageSource(_viewModel?.ImageUri);
                ResetZoom();
            }
        }

        private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_viewModel == null || e.Handled || _zoom > 1.01)
            {
                return;
            }

            _viewModel.IsChromeVisible = !_viewModel.IsChromeVisible;
        }

        private void TopTapZone_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_viewModel == null || _zoom > 1.01)
            {
                return;
            }

            _viewModel.IsChromeVisible = true;
            e.Handled = true;
        }

        private void Chrome_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void ViewerImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            CaptureNaturalSize();
            UpdateImageLayout();
            ResetZoom();
        }

        private void ImageArea_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (ImageTransform == null)
            {
                return;
            }

            bool scaled = Math.Abs(e.Delta.Scale - 1.0) > 0.001;
            if (scaled)
            {
                var origin = e.Position;
                ZoomAt(ImageTransform.ScaleX * e.Delta.Scale, origin.X, origin.Y);
            }

            // Pan only when zoomed (or during an in-progress pinch above 1x).
            if (_zoom > 1.01 || scaled)
            {
                ImageTransform.TranslateX += e.Delta.Translation.X;
                ImageTransform.TranslateY += e.Delta.Translation.Y;
                ClampTranslation();
            }

            UpdatePanOverlays();
            e.Handled = true;
        }

        private void ImageArea_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            ClampTranslation();
            UpdatePanOverlays();
        }

        private void ImageArea_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var pos = e.GetPosition(ImageArea);
            if (_zoom > 1.05)
            {
                ZoomAt(MinZoom, pos.X, pos.Y);
            }
            else
            {
                ZoomAt(2.5, pos.X, pos.Y);
            }

            e.Handled = true;
        }

        private void ImageArea_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(ImageArea);
            if (point.Properties.IsHorizontalMouseWheel)
            {
                return;
            }

            int delta = point.Properties.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }

            double factor = delta > 0 ? WheelZoomStep : 1.0 / WheelZoomStep;
            ZoomAt(_zoom * factor, point.Position.X, point.Position.Y);
            e.Handled = true;
        }

        private void ZoomAt(double targetZoom, double viewportX, double viewportY)
        {
            targetZoom = Clamp(targetZoom, MinZoom, MaxZoom);
            if (Math.Abs(targetZoom - _zoom) < 0.001)
            {
                if (targetZoom <= MinZoom + 0.01)
                {
                    ResetZoom();
                }

                return;
            }

            // Content point under the finger/cursor before zoom.
            double contentX = (viewportX - ImageTransform.TranslateX) / _zoom;
            double contentY = (viewportY - ImageTransform.TranslateY) / _zoom;

            _zoom = targetZoom;
            ImageTransform.ScaleX = _zoom;
            ImageTransform.ScaleY = _zoom;

            ImageTransform.TranslateX = viewportX - contentX * _zoom;
            ImageTransform.TranslateY = viewportY - contentY * _zoom;

            if (_zoom <= MinZoom + 0.01)
            {
                ResetZoom();
                return;
            }

            ClampTranslation();
            UpdatePanOverlays();
        }

        private void ClampTranslation()
        {
            if (ImageArea == null || ImageTransform == null || _displayWidth <= 0 || _displayHeight <= 0)
            {
                return;
            }

            double vw = ImageArea.ActualWidth;
            double vh = ImageArea.ActualHeight;
            if (vw <= 0 || vh <= 0)
            {
                return;
            }

            double scaledW = _displayWidth * _zoom;
            double scaledH = _displayHeight * _zoom;

            // Host fills the area; scale around center → clamp to half the overflow.
            if (scaledW <= vw)
            {
                ImageTransform.TranslateX = 0;
            }
            else
            {
                double maxX = (scaledW - vw) / 2.0;
                ImageTransform.TranslateX = Clamp(ImageTransform.TranslateX, -maxX, maxX);
            }

            if (scaledH <= vh)
            {
                ImageTransform.TranslateY = 0;
            }
            else
            {
                double maxY = (scaledH - vh) / 2.0;
                ImageTransform.TranslateY = Clamp(ImageTransform.TranslateY, -maxY, maxY);
            }
        }

        private void UpdatePanOverlays()
        {
            bool canPan = _zoom > 1.01;
            if (TopTapZone != null)
            {
                TopTapZone.IsHitTestVisible = !canPan;
            }

            if (canPan)
            {
                // Full-bleed chrome would steal pans — hide it (Imgur does the same).
                if (_viewModel != null && _viewModel.IsChromeVisible)
                {
                    _viewModel.IsChromeVisible = false;
                }
                else if (TopChrome != null)
                {
                    TopChrome.IsHitTestVisible = false;
                }
            }
        }

        private void ResetZoom()
        {
            _zoom = MinZoom;
            if (ImageTransform != null)
            {
                ImageTransform.ScaleX = MinZoom;
                ImageTransform.ScaleY = MinZoom;
                ImageTransform.TranslateX = 0;
                ImageTransform.TranslateY = 0;
            }

            UpdatePanOverlays();
        }

        private void LoadImageSource(string uri)
        {
            _naturalWidth = 0;
            _naturalHeight = 0;
            _displayWidth = 0;
            _displayHeight = 0;
            if (string.IsNullOrWhiteSpace(uri))
            {
                ViewerImage.Source = null;
                return;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.UriSource = new Uri(uri.Trim());
                ViewerImage.Source = bmp;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ImageViewerView] Load: " + ex.Message);
                ViewerImage.Source = null;
            }
        }

        private void CaptureNaturalSize()
        {
            var bmp = ViewerImage.Source as BitmapImage;
            if (bmp == null)
            {
                return;
            }

            if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
            {
                _naturalWidth = bmp.PixelWidth;
                _naturalHeight = bmp.PixelHeight;
            }
        }

        private void UpdateImageLayout()
        {
            if (ViewerImage == null || ImageArea == null)
            {
                return;
            }

            double viewportW = ImageArea.ActualWidth;
            double viewportH = ImageArea.ActualHeight;
            if (viewportW <= 0 || viewportH <= 0)
            {
                return;
            }

            CaptureNaturalSize();
            double naturalW = _naturalWidth;
            double naturalH = _naturalHeight;
            if (naturalW <= 0 || naturalH <= 0)
            {
                return;
            }

            // Fit without upscaling — only shrink if larger than the viewport.
            double scale = Math.Min(1.0, Math.Min(viewportW / naturalW, viewportH / naturalH));
            _displayWidth = naturalW * scale;
            _displayHeight = naturalH * scale;

            ViewerImage.Width = _displayWidth;
            ViewerImage.Height = _displayHeight;
            ViewerImage.Stretch = Stretch.Uniform;

            ClampTranslation();
        }

        private void ApplyChromeVisibility(bool visible, bool animate)
        {
            // While zoomed, ignore chrome-show requests so pan stays free.
            if (visible && _zoom > 1.01)
            {
                visible = false;
            }

            double target = visible ? 1.0 : 0.0;
            bool showCaption = visible &&
                               _viewModel != null &&
                               _viewModel.HasCaption;

            if (!animate)
            {
                TopChrome.Opacity = target;
                TopChrome.IsHitTestVisible = visible;
                ApplyCaptionOverlay(showCaption, animate: false);
                return;
            }

            AnimateOpacity(TopChrome, target);
            TopChrome.IsHitTestVisible = visible;
            ApplyCaptionOverlay(showCaption, animate: true);
        }

        private void ApplyCaptionOverlay(bool show, bool animate)
        {
            if (CaptionChrome == null)
            {
                return;
            }

            CaptionChrome.IsHitTestVisible = show;
            if (!show)
            {
                if (!animate)
                {
                    CaptionChrome.Opacity = 0;
                    CaptionChrome.Visibility = Visibility.Collapsed;
                    return;
                }

                AnimateOpacity(CaptionChrome, 0);
                // Keep in tree until fade ends so we don't pop layout mid-frame.
                CaptionChrome.Visibility = Visibility.Visible;
                return;
            }

            CaptionChrome.Visibility = Visibility.Visible;
            if (!animate)
            {
                CaptionChrome.Opacity = 1;
                return;
            }

            CaptionChrome.Opacity = 0;
            AnimateOpacity(CaptionChrome, 1);
        }

        private static void AnimateOpacity(UIElement element, double to)
        {
            if (element == null)
            {
                return;
            }

            var sb = new Storyboard();
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, element);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
            sb.Begin();
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }
    }
}
