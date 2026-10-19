using Unison.Core.Models;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Status list circle: thumb/avatar with a green (Recent) or gray (Viewed) ring.
    /// </summary>
    public sealed partial class StatusListAvatarControl : UserControl
    {
        private static readonly SolidColorBrush RecentRingBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0xD3, 0x66));

        private static readonly SolidColorBrush ViewedRingBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

        public static readonly DependencyProperty AvatarUrlProperty =
            DependencyProperty.Register(
                nameof(AvatarUrl),
                typeof(string),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(null, OnAvatarUrlChanged));

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

        private static void OnAvatarUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as StatusListAvatarControl;
            if (control != null)
            {
                control.ApplyAvatar();
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
            ApplyAvatar();
        }

        private void ApplyAvatar()
        {
            if (InnerAvatar == null)
            {
                return;
            }

            InnerAvatar.AvatarUrl = AvatarUrl;
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
            double inner = System.Math.Max(24d, outer - 8d);
            if (RootGrid != null)
            {
                RootGrid.Width = outer;
                RootGrid.Height = outer;
            }

            if (Ring != null)
            {
                Ring.Width = outer;
                Ring.Height = outer;
            }

            if (InnerAvatar != null)
            {
                InnerAvatar.Width = inner;
                InnerAvatar.Height = inner;
                InnerAvatar.Size = inner;
            }
        }
    }
}
