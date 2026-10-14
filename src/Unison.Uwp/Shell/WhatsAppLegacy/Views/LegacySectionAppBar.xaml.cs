using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Bottom bar for Legacy pivots that only open search chrome, plus profile and debug.
    /// </summary>
    public sealed partial class LegacySectionAppBar : UserControl
    {
        public event EventHandler SearchRequested;

        public event EventHandler ProfileRequested;

        public event EventHandler DebugRequested;

        public LegacySectionAppBar()
        {
            InitializeComponent();
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            SearchRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Profile_Click(object sender, RoutedEventArgs e)
        {
            ProfileRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Debug_Click(object sender, RoutedEventArgs e)
        {
            DebugRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Bar_Loaded(object sender, RoutedEventArgs e)
        {
            var bar = sender as CommandBar;
            if (bar == null)
            {
                return;
            }

            LegacyCommandBarLayout.CenterPrimaryCommands(bar);
        }
    }
}
