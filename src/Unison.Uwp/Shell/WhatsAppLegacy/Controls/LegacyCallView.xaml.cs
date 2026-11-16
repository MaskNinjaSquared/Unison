using System;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Controls
{
    /// <summary>
    /// Call screen body. Keeps the photo and the dock keys square to the screen width and
    /// drives the call clock once a second while shown.
    /// </summary>
    public sealed partial class LegacyCallView : UserControl
    {
        private const int KeyColumns = 3;

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

            double key = Math.Floor((width - KeyDock.Margin.Left - KeyDock.Margin.Right) / KeyColumns);
            KeyRowTop.Height = new GridLength(key);
            KeyRowBottom.Height = new GridLength(key);

            // Landscape / desktop: the photo gives way so the dock stays on screen.
            double dock = (2 * key) + KeyDock.Margin.Top + KeyDock.Margin.Bottom;
            PhotoArea.Height = Math.Max(0, Math.Min(width, e.NewSize.Height - dock));
        }
    }
}
