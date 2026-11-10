using System;
using Unison.Core.Models;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Status list circle: ring + face selected by kind (text color plate vs
    /// <see cref="ChatAvatarControl"/> photo thumb).
    /// </summary>
    public sealed partial class StatusListAvatarControl : UserControl
    {
        private static readonly Brush RecentRingBrush =
            (Application.Current?.Resources["StatusRecentRingBrush"] as Brush)
            ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0xD3, 0x66));

        private static readonly SolidColorBrush ViewedRingBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

        private double _innerSize = 48d;

        public static readonly DependencyProperty AvatarUrlProperty =
            DependencyProperty.Register(
                nameof(AvatarUrl),
                typeof(string),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(null, OnFaceChanged));

        public static readonly DependencyProperty ViewStateProperty =
            DependencyProperty.Register(
                nameof(ViewState),
                typeof(StatusViewState),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(StatusViewState.Recent, OnViewStateChanged));

        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register(
                nameof(Size),
                typeof(double),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(56d, OnSizeChanged));

        public static readonly DependencyProperty ShowRingProperty =
            DependencyProperty.Register(
                nameof(ShowRing),
                typeof(bool),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(true, OnSizeChanged));

        public static readonly DependencyProperty KindProperty =
            DependencyProperty.Register(
                nameof(Kind),
                typeof(ChatPreviewKind),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(ChatPreviewKind.Image, OnFaceChanged));

        public static readonly DependencyProperty BackgroundArgbProperty =
            DependencyProperty.Register(
                nameof(BackgroundArgb),
                typeof(uint),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(0u, OnFaceChanged));

        public static readonly DependencyProperty TextArgbProperty =
            DependencyProperty.Register(
                nameof(TextArgb),
                typeof(uint),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(0u, OnFaceChanged));

        public static readonly DependencyProperty PreviewTextProperty =
            DependencyProperty.Register(
                nameof(PreviewText),
                typeof(string),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(null, OnFaceChanged));

        public StatusListAvatarControl()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyAll();
        }

        public string AvatarUrl
        {
            get { return (string)GetValue(AvatarUrlProperty); }
            set { SetValue(AvatarUrlProperty, value); }
        }

        public StatusViewState ViewState
        {
            get { return (StatusViewState)GetValue(ViewStateProperty); }
            set { SetValue(ViewStateProperty, value); }
        }

        public double Size
        {
            get { return (double)GetValue(SizeProperty); }
            set { SetValue(SizeProperty, value); }
        }

        /// <summary>False for the own-status strip (no Recent/Viewed ring).</summary>
        public bool ShowRing
        {
            get { return (bool)GetValue(ShowRingProperty); }
            set { SetValue(ShowRingProperty, value); }
        }

        public ChatPreviewKind Kind
        {
            get { return (ChatPreviewKind)GetValue(KindProperty); }
            set { SetValue(KindProperty, value); }
        }

        public uint BackgroundArgb
        {
            get { return (uint)GetValue(BackgroundArgbProperty); }
            set { SetValue(BackgroundArgbProperty, value); }
        }

        public uint TextArgb
        {
            get { return (uint)GetValue(TextArgbProperty); }
            set { SetValue(TextArgbProperty, value); }
        }

        public string PreviewText
        {
            get { return (string)GetValue(PreviewTextProperty); }
            set { SetValue(PreviewTextProperty, value); }
        }

        private static void OnFaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as StatusListAvatarControl;
            if (control != null)
            {
                control.ApplyFace();
            }
        }

        private static void OnViewStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as StatusListAvatarControl;
            if (control != null)
            {
                control.ApplyRing();
            }
        }

        private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as StatusListAvatarControl;
            if (control != null)
            {
                control.ApplySize();
            }
        }

        private void ApplyAll()
        {
            ApplySize();
            ApplyRing();
            ApplyFace();
        }

        private void ApplyFace()
        {
            if (FaceHost == null)
            {
                return;
            }

            // New instance so ContentControl re-runs the template selector when Kind flips.
            FaceHost.Content = new StatusListAvatarFace
            {
                Kind = Kind,
                AvatarUrl = AvatarUrl,
                BackgroundArgb = BackgroundArgb,
                TextArgb = TextArgb,
                PreviewText = PreviewText,
                Size = _innerSize
            };
        }

        private void ApplyRing()
        {
            if (Ring == null)
            {
                return;
            }

            Ring.Stroke = ViewState == StatusViewState.Viewed
                ? ViewedRingBrush
                : RecentRingBrush;
        }

        private void ApplySize()
        {
            double outer = Size > 0 ? Size : 56d;
            bool showRing = ShowRing;
            double inner = showRing ? Math.Max(24d, outer - 8d) : outer;

            if (RootGrid != null)
            {
                RootGrid.Width = outer;
                RootGrid.Height = outer;
            }

            if (Ring != null)
            {
                Ring.Width = outer;
                Ring.Height = outer;
                Ring.Visibility = showRing ? Visibility.Visible : Visibility.Collapsed;
            }

            _innerSize = inner;
            if (FaceHost != null)
            {
                FaceHost.Width = inner;
                FaceHost.Height = inner;
            }

            // Restamp face Size when the hole diameter changes.
            if (FaceHost != null && FaceHost.Content != null)
            {
                ApplyFace();
            }
        }
    }
}
