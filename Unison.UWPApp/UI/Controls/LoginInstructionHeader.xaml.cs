using System;
using Windows.UI.Xaml.Controls;

namespace Unison.UWPApp.UI.Controls
{
    public sealed partial class LoginInstructionHeader : UserControl
    {
        public event EventHandler BackRequested;

        public LoginInstructionHeader()
        {
            this.InitializeComponent();
        }
    }
}
