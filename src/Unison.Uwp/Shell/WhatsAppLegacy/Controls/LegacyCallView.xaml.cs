using System;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Controls
{
    /// <summary>
    /// Call screen body. Keeps the photo roughly square to the screen width, shortens
    /// the dock keys, and drives the call clock once a second while shown.
    /// </summary>
    public sealed partial class LegacyCallView : UserControl
    {
        private const int KeyColumns = 3;
        /// <summary>Shorter than a full square key — Answer / Decline / Reply stay compact.</summary>
        private const double KeyHeightRatio = 0.48;

        private readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        public LegacyCallView()
        {
            InitializeComponent();
            _clock.Tick += (s, e) => (DataContext as CallScreenViewModel)?.RefreshClock();
            Loaded += (s, e) => _clock.Start();
            Unloaded += (s, e) => _clock.Stop();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double width = e.NewSize.Width;
            if (width <= 0)
            {
                return;
            }

            double keyWidth = Math.Floor((width - KeyDock.Margin.Left - KeyDock.Margin.Right) / KeyColumns);
            double keyHeight = Math.Floor(keyWidth * KeyHeightRatio);
            KeyRowTop.Height = new GridLength(keyHeight);
            KeyRowBottom.Height = new GridLength(keyHeight);
            RingKeyRowTop.Height = new GridLength(keyHeight);
            RingKeyRowBottom.Height = new GridLength(keyHeight);

            // Landscape / desktop: the photo gives way so the dock stays on screen.
            double dock = (2 * keyHeight) + KeyDock.Margin.Top + KeyDock.Margin.Bottom;
            PhotoArea.Height = Math.Max(0, Math.Min(width, e.NewSize.Height - dock));
        }
    }
}
