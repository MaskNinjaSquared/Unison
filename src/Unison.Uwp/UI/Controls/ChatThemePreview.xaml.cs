// =============================================================================
// ChatThemePreview
//
// Form-factor preview (phone vs PC) via template selector. Wallpaper knobs
// (kind / doodles / solid color) push into ChatThemePreviewState so the
// ChatBackgroundSurface inside the template binds them live.
// =============================================================================
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Uwp.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class ChatThemePreview : UserControl
    {
        public static readonly DependencyProperty BackgroundKindProperty =
            DependencyProperty.Register(
                nameof(BackgroundKind),
                typeof(ChatBackgroundKind),
                typeof(ChatThemePreview),
                new PropertyMetadata(ChatBackgroundKind.Default, OnBackgroundChanged));

        public static readonly DependencyProperty CustomPathProperty =
            DependencyProperty.Register(
                nameof(CustomPath),
                typeof(string),
                typeof(ChatThemePreview),
                new PropertyMetadata(null, OnBackgroundChanged));

        public static readonly DependencyProperty ImageUriProperty =
            DependencyProperty.Register(
                nameof(ImageUri),
                typeof(string),
                typeof(ChatThemePreview),
                new PropertyMetadata(null, OnBackgroundChanged));

        public static readonly DependencyProperty SolidColorHexProperty =
            DependencyProperty.Register(
                nameof(SolidColorHex),
                typeof(string),
                typeof(ChatThemePreview),
                new PropertyMetadata(null, OnBackgroundChanged));

        private readonly ChatThemePreviewState _state = new ChatThemePreviewState();
        private Border _previewRoot;
        private bool _formFactorReady;

        public ChatThemePreview()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, __) => UpdateClip();
        }

        public ChatBackgroundKind BackgroundKind
        {
            get { return (ChatBackgroundKind)GetValue(BackgroundKindProperty); }
            set { SetValue(BackgroundKindProperty, value); }
        }

        public string CustomPath
        {
            get { return (string)GetValue(CustomPathProperty); }
            set { SetValue(CustomPathProperty, value); }
        }

        public string ImageUri
        {
            get { return (string)GetValue(ImageUriProperty); }
            set { SetValue(ImageUriProperty, value); }
        }

        public string SolidColorHex
        {
            get { return (string)GetValue(SolidColorHexProperty); }
            set { SetValue(SolidColorHexProperty, value); }
        }

        private static void OnBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var preview = d as ChatThemePreview;
            preview?.PushState();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureFormFactorHost();
            PushState();
            UpdateClip();
        }

        private void EnsureFormFactorHost()
        {
            if (FormFactorHost == null)
            {
                return;
            }

            bool mobile = ResolveIsMobile();
            _state.IsMobile = mobile;

            if (!_formFactorReady || !ReferenceEquals(FormFactorHost.Content, _state))
            {
                FormFactorHost.Content = _state;
                _formFactorReady = true;
                FormFactorHost.UpdateLayout();
            }

            _previewRoot = FindVisualChild<Border>(FormFactorHost);
            if (_previewRoot == null)
            {
                _ = Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () =>
                    {
                        _previewRoot = FindVisualChild<Border>(FormFactorHost);
                        UpdateClip();
                    });
            }
        }

        private void PushState()
        {
            EnsureFormFactorHost();
            _state.BackgroundKind = BackgroundKind;
            _state.CustomPath = CustomPath;
            _state.SolidColorHex = SolidColorHex;
        }

        private static bool ResolveIsMobile()
        {
            try
            {
                var systemInfo = App.Services?.GetService<ISystemInfoProvider>();
                if (systemInfo != null)
                {
                    return systemInfo.IsMobile();
                }
            }
            catch
            {
            }

            return SystemInfoProvider.DetectIsMobile();
        }

        private void UpdateClip()
        {
            if (_previewRoot == null)
            {
                _previewRoot = FindVisualChild<Border>(FormFactorHost);
            }

            if (_previewRoot == null ||
                _previewRoot.ActualWidth <= 0 ||
                _previewRoot.ActualHeight <= 0)
            {
                return;
            }

            _previewRoot.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(
                    0,
                    0,
                    _previewRoot.ActualWidth,
                    _previewRoot.ActualHeight)
            };
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }

                T nested = FindVisualChild<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
