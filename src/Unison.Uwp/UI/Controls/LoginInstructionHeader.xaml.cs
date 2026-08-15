using System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Localized pairing instructions. Dev easter-egg (5 taps) raises
    /// <see cref="SessionResetRequested"/> — the host clears the session.
    /// </summary>
    public sealed partial class LoginInstructionHeader : UserControl
    {
        private int _tapCount;

        public LoginInstructionHeader()
        {
            this.InitializeComponent();
        }

        /// <summary>Raised after five taps on the instruction block (hidden wipe).</summary>
        public event EventHandler SessionResetRequested;

        private void Root_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _tapCount++;
            if (_tapCount < 5)
            {
                return;
            }

            _tapCount = 0;
            SessionResetRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
