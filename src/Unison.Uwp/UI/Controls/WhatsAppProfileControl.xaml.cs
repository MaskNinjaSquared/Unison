using System;
using System.Windows.Input;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// WhatsApp settings profile section: photo and the name, phone, and about rows.
    /// </summary>
    public sealed partial class WhatsAppProfileControl : UserControl
    {
        public WhatsAppProfileControl()
        {
            InitializeComponent();
            Loaded += WhatsAppProfileControl_Loaded;
        }

        public static readonly DependencyProperty IsLegacyChromeProperty =
            DependencyProperty.Register(
                nameof(IsLegacyChrome),
                typeof(bool),
                typeof(WhatsAppProfileControl),
                new PropertyMetadata(false, OnIsLegacyChromeChanged));

        /// <summary>Forwards to the edit overlay so Legacy gets a CommandBar save chrome.</summary>
        public bool IsLegacyChrome
        {
            get => (bool)GetValue(IsLegacyChromeProperty);
            set => SetValue(IsLegacyChromeProperty, value);
        }

        private static void OnIsLegacyChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (WhatsAppProfileControl)d;
            if (control.EditOverlay != null)
            {
                control.EditOverlay.IsLegacyChrome = (bool)e.NewValue;
            }
        }

        private void WhatsAppProfileControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (EditOverlay != null)
            {
                EditOverlay.IsLegacyChrome = IsLegacyChrome;
            }
        }

        private void NameBox_Click(object sender, EventArgs e)
        {
            Execute(DataContext as SettingsViewModel, vm => vm.OpenProfileNameEditCommand);
        }

        private void AboutBox_Click(object sender, EventArgs e)
        {
            Execute(DataContext as SettingsViewModel, vm => vm.OpenProfileAboutEditCommand);
        }

        private static void Execute(SettingsViewModel vm, Func<SettingsViewModel, ICommand> pick)
        {
            if (vm == null)
            {
                return;
            }

            ICommand command = pick(vm);
            if (command != null && command.CanExecute(null))
            {
                command.Execute(null);
            }
        }
    }
}
