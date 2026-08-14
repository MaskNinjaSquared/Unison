using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class LinkPhoneText : UserControl
    {
        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register(
                nameof(Command),
                typeof(System.Windows.Input.ICommand),
                typeof(LinkPhoneText),
                new PropertyMetadata(null));

        public LinkPhoneText()
        {
            this.InitializeComponent();
            this.Tapped += LinkPhoneText_Tapped;
        }

        public System.Windows.Input.ICommand Command
        {
            get => (System.Windows.Input.ICommand)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        private void LinkPhoneText_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (Command != null && Command.CanExecute(null))
            {
                Command.Execute(null);
            }
        }
    }
}
