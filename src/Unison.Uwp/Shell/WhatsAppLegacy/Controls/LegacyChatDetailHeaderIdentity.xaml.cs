using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Controls
{
    /// <summary>
    /// Legacy chat-detail header identity: no contact/group photo. Visible only for
    /// community announcements (Avisos), as a megaphone plate.
    /// </summary>
    public sealed partial class LegacyChatDetailHeaderIdentity : UserControl
    {
        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register(
                nameof(Size),
                typeof(double),
                typeof(LegacyChatDetailHeaderIdentity),
                new PropertyMetadata(40d, OnSizeChanged));

        public static readonly DependencyProperty IsAnnouncementsProperty =
            DependencyProperty.Register(
                nameof(IsAnnouncements),
                typeof(bool),
                typeof(LegacyChatDetailHeaderIdentity),
                new PropertyMetadata(false, OnIsAnnouncementsChanged));

        public LegacyChatDetailHeaderIdentity()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplySize();
        }

        public double Size
        {
            get { return (double)GetValue(SizeProperty); }
            set { SetValue(SizeProperty, value); }
        }

        /// <summary>True when the open chat is a community Avisos group.</summary>
        public bool IsAnnouncements
        {
            get { return (bool)GetValue(IsAnnouncementsProperty); }
            set { SetValue(IsAnnouncementsProperty, value); }
        }

        private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as LegacyChatDetailHeaderIdentity)?.ApplySize();
        }

        private static void OnIsAnnouncementsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as LegacyChatDetailHeaderIdentity;
            if (control == null)
            {
                return;
            }

            control.Visibility = control.IsAnnouncements
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ApplySize()
        {
            if (RootGrid == null || Plate == null || Megaphone == null)
            {
                return;
            }

            double size = Size > 0 ? Size : 40d;
            RootGrid.Width = size;
            RootGrid.Height = size;
            Plate.Width = size;
            Plate.Height = size;
            Megaphone.FontSize = Math.Max(12, size * 0.42);
        }
    }
}
