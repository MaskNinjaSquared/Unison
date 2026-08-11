using System;
using System.ComponentModel;
using Unison.Core.ViewModels;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Full-screen image overlay (Imgur-inspired chrome).
    /// Shows media at native size (downscale-to-fit only); zoom only via user gesture.
    /// Caption lives in a dedicated Auto row below the image.
    /// </summary>
    public sealed partial class ImageViewerView : UserControl
    {
        private const float MinZoom = 1f;
        private const float MaxZoom = 5f;
        private const float WheelZoomStep = 1.12f;

        private ImageViewerViewModel _viewModel;
        private int _naturalWidth;
        private int _naturalHeight;

        public ImageViewerView()
        {
            InitializeComponent();
            Unloaded += ImageViewerView_Unloaded;
            SizeChanged += ImageViewerView_SizeChanged;
            ImageScroll.SizeChanged += ImageScroll_SizeChanged;

            RootGrid.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(ImageScroll_PointerWheelChanged),
                true /* handledEventsToo */);
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
                DataContext = value;
                _naturalWidth = 0;
                _naturalHeight = 0;

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

        private void ImageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
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
            if (_viewModel == null || e.Handled)
            {
                return;
            }

            _viewModel.IsChromeVisible = !_viewModel.IsChromeVisible;
        }

        private void Chrome_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void ViewerImage_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ImageScroll == null)
            {
                return;
            }

            float target = ImageScroll.ZoomFactor > 1.05f ? MinZoom : 2.5f;
            Point pos = e.GetPosition(ImageScroll);
            float oldZoom = ImageScroll.ZoomFactor;
            double contentX = (ImageScroll.HorizontalOffset + pos.X) / oldZoom;
            double contentY = (ImageScroll.VerticalOffset + pos.Y) / oldZoom;
            double newHorizontal = contentX * target - pos.X;
            double newVertical = contentY * target - pos.Y;
            ImageScroll.ChangeView(newHorizontal, newVertical, target, disableAnimation: false);
            UpdateScrollModesForZoom(target);
            e.Handled = true;
        }

        private void ImageScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (ImageScroll == null || ImageScroll.Visibility != Visibility.Visible)
            {
                return;
            }

            var point = e.GetCurrentPoint(ImageScroll);
            if (point.Properties.IsHorizontalMouseWheel)
            {
                return;
            }

            int delta = point.Properties.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }

            float oldZoom = ImageScroll.ZoomFactor;
            float newZoom = delta > 0
                ? oldZoom * WheelZoomStep
                : oldZoom / WheelZoomStep;
            newZoom = Clamp(newZoom, MinZoom, MaxZoom);
            e.Handled = true;
            if (Math.Abs(newZoom - oldZoom) < 0.001f)
            {
                return;
            }

            Point pos = point.Position;
            if (pos.X < 0 || pos.Y < 0 ||
                pos.X > ImageScroll.ActualWidth ||
                pos.Y > ImageScroll.ActualHeight)
            {
                pos = new Point(ImageScroll.ActualWidth / 2, ImageScroll.ActualHeight / 2);
            }

            double contentX = (ImageScroll.HorizontalOffset + pos.X) / oldZoom;
            double contentY = (ImageScroll.VerticalOffset + pos.Y) / oldZoom;
            double newHorizontal = contentX * newZoom - pos.X;
            double newVertical = contentY * newZoom - pos.Y;

            ImageScroll.ChangeView(newHorizontal, newVertical, newZoom, disableAnimation: true);
            UpdateScrollModesForZoom(newZoom);
        }

        private void UpdateScrollModesForZoom(float zoom)
        {
            bool canPan = zoom > 1.01f;
            ImageScroll.HorizontalScrollMode = canPan
                ? ScrollMode.Enabled
                : ScrollMode.Disabled;
            ImageScroll.VerticalScrollMode = canPan
                ? ScrollMode.Enabled
                : ScrollMode.Disabled;
        }

        private void ViewerImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            CaptureNaturalSize();
            UpdateImageLayout();
            ResetZoom();
        }

        private void ResetZoom()
        {
            if (ImageScroll == null)
            {
                return;
            }

            ImageScroll.ChangeView(0, 0, MinZoom, disableAnimation: true);
            UpdateScrollModesForZoom(MinZoom);
        }

        /// <summary>
        /// Full-resolution decode (no DecodePixelWidth) so layout uses native media size.
        /// </summary>
        private void LoadImageSource(string uri)
        {
            _naturalWidth = 0;
            _naturalHeight = 0;
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
            if (ViewerImage == null || ImageScroll == null || ImageHost == null)
            {
                return;
            }

            double viewportW = ImageScroll.ActualWidth;
            double viewportH = ImageScroll.ActualHeight;
            if (viewportW <= 0)
            {
                viewportW = ImageScroll.ViewportWidth;
            }

            if (viewportH <= 0)
            {
                viewportH = ImageScroll.ViewportHeight;
            }

            if (viewportW <= 0 || viewportH <= 0)
            {
                return;
            }

            CaptureNaturalSize();
            double naturalW = _naturalWidth;
            double naturalH = _naturalHeight;
            if (naturalW <= 0 || naturalH <= 0)
            {
                // Until PixelWidth is known, host the viewport so we stay centered.
                ImageHost.Width = viewportW;
                ImageHost.Height = viewportH;
                return;
            }

            // Fit inside viewport without upscaling — only shrink if larger than screen.
            double scale = Math.Min(1.0, Math.Min(viewportW / naturalW, viewportH / naturalH));
            double displayW = naturalW * scale;
            double displayH = naturalH * scale;

            ViewerImage.Width = displayW;
            ViewerImage.Height = displayH;
            ViewerImage.Stretch = Windows.UI.Xaml.Media.Stretch.Uniform;
            ViewerImage.HorizontalAlignment = HorizontalAlignment.Center;
            ViewerImage.VerticalAlignment = VerticalAlignment.Center;

            // Host == viewport so Center alignment places the bitmap in the middle.
            ImageHost.Width = viewportW;
            ImageHost.Height = viewportH;
        }

        private void ApplyChromeVisibility(bool visible, bool animate)
        {
            double target = visible ? 1.0 : 0.0;

            if (!animate)
            {
                TopChrome.Opacity = target;
                TopChrome.IsHitTestVisible = visible;
                ApplyCaptionRowVisibility(visible);
                return;
            }

            AnimateOpacity(TopChrome, target);
            TopChrome.IsHitTestVisible = visible;
            ApplyCaptionRowVisibility(visible);
        }

        private void ApplyCaptionRowVisibility(bool chromeVisible)
        {
            if (CaptionChrome == null)
            {
                return;
            }

            // Keep Auto-row honest: collapse when chrome hidden so it doesn't leave a gap.
            bool show = chromeVisible &&
                        _viewModel != null &&
                        _viewModel.HasCaption;
            CaptionChrome.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            CaptionChrome.Opacity = 1;
            CaptionChrome.IsHitTestVisible = show;
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

        private static float Clamp(float value, float min, float max)
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
