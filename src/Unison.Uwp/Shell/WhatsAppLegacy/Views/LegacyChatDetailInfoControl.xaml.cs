using System.ComponentModel;
using System.Windows.Input;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Controls;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Legacy info pane. Same body as <c>ChatDetailInfoControl</c>; the bottom bar uses the
    /// legacy accent, with circled icons centered and more on the right.
    /// </summary>
    public sealed partial class LegacyChatDetailInfoControl : UserControl
    {
        public static readonly DependencyProperty InfoViewModelProperty =
            DependencyProperty.Register(
                nameof(InfoViewModel),
                typeof(ChatDetailInfoViewModel),
                typeof(LegacyChatDetailInfoControl),
                new PropertyMetadata(null, OnInfoViewModelChanged));

        public static readonly DependencyProperty CloseCommandProperty =
            DependencyProperty.Register(
                nameof(CloseCommand),
                typeof(ICommand),
                typeof(LegacyChatDetailInfoControl),
                new PropertyMetadata(null));

        private ChatDetailInfoViewModel _boundInfo;
        private bool _infoHooked;
        private bool _isLoaded;
        private ChatDetailUserInfoControl _userInfoHost;
        private ChatDetailGroupInfoControl _groupInfoHost;
        private bool _captionsOpen;

        public LegacyChatDetailInfoControl()
        {
            InitializeComponent();
            EnsureHosts();
            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
        }

        public ChatDetailInfoViewModel InfoViewModel
        {
            get { return (ChatDetailInfoViewModel)GetValue(InfoViewModelProperty); }
            set { SetValue(InfoViewModelProperty, value); }
        }

        public ICommand CloseCommand
        {
            get { return (ICommand)GetValue(CloseCommandProperty); }
            set { SetValue(CloseCommandProperty, value); }
        }

        private static void OnInfoViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as LegacyChatDetailInfoControl;
            control?.OnInfoViewModelChanged(e.OldValue as ChatDetailInfoViewModel, e.NewValue as ChatDetailInfoViewModel);
        }

        private void OnInfoViewModelChanged(ChatDetailInfoViewModel oldVm, ChatDetailInfoViewModel newVm)
        {
            UnhookInfo();
            _boundInfo = newVm;
            HookInfo();
            ApplyInfoViewModel();
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            HookInfo();
            ApplyInfoViewModel();
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            UnhookInfo();
        }

        private void HookInfo()
        {
            if (_boundInfo == null || _infoHooked || !_isLoaded)
            {
                return;
            }

            _boundInfo.PropertyChanged += Info_PropertyChanged;
            _infoHooked = true;
        }

        private void UnhookInfo()
        {
            if (_boundInfo == null || !_infoHooked)
            {
                return;
            }

            _boundInfo.PropertyChanged -= Info_PropertyChanged;
            _infoHooked = false;
        }

        private void Info_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ApplyInfoViewModel();
        }

        private void EnsureHosts()
        {
            if (_userInfoHost != null && _groupInfoHost != null)
            {
                return;
            }

            if (InfoBodyHost == null)
            {
                return;
            }

            foreach (var child in InfoBodyHost.Children)
            {
                if (_userInfoHost == null)
                {
                    _userInfoHost = child as ChatDetailUserInfoControl;
                }

                if (_groupInfoHost == null)
                {
                    _groupInfoHost = child as ChatDetailGroupInfoControl;
                }
            }

            ApplyAccentToggle(_userInfoHost);
            ApplyAccentToggle(_groupInfoHost);
        }

        /// <summary>
        /// The shared info body paints the switch track white. On this pane the on-state is the legacy accent.
        /// </summary>
        private static void ApplyAccentToggle(FrameworkElement host)
        {
            if (host == null)
            {
                return;
            }

            var on = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x65, 0x6A));
            var hover = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x7A, 0x80));
            var pressed = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x52, 0x56));
            host.Resources["ToggleSwitchFillOn"] = on;
            host.Resources["ToggleSwitchFillOnPointerOver"] = hover;
            host.Resources["ToggleSwitchFillOnPressed"] = pressed;
            host.Resources["ToggleSwitchStrokeOn"] = on;
            host.Resources["ToggleSwitchStrokeOnPointerOver"] = hover;
            host.Resources["ToggleSwitchStrokeOnPressed"] = pressed;
        }

        private void ApplyInfoViewModel()
        {
            EnsureHosts();
            var vm = InfoViewModel;

            if (_userInfoHost != null)
            {
                _userInfoHost.InfoViewModel = vm != null && vm.IsUser ? vm : null;
                _userInfoHost.Visibility = vm != null && vm.IsUser ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_groupInfoHost != null)
            {
                _groupInfoHost.InfoViewModel = vm != null && vm.IsGroup ? vm : null;
                _groupInfoHost.Visibility = vm != null && vm.IsGroup ? Visibility.Visible : Visibility.Collapsed;
            }

            if (vm == null)
            {
                return;
            }

            if (AddContactButton != null)
            {
                bool canAdd = vm.CanAddToAddressBook;
                AddContactButton.Visibility = canAdd ? Visibility.Visible : Visibility.Collapsed;
                AddContactButton.IsEnabled = canAdd && vm.AddContactCommand?.CanExecute(null) == true;
            }

            if (ChatPinButton != null)
            {
                if (ChatPinCaption != null)
                {
                    ChatPinCaption.Text = vm.IsChatPinned ? "unpin chat" : "pin chat";
                }

                ChatPinButton.IsEnabled = vm.PinChatCommand?.CanExecute(null) == true;
            }

            if (PinButton != null)
            {
                if (PinCaption != null)
                {
                    PinCaption.Text = vm.IsWidgetPinned ? "unpin from start" : "pin to start";
                }

                PinButton.IsEnabled = vm.PinToStartCommand?.CanExecute(null) == true;
            }

            if (DeleteChatButton != null)
            {
                DeleteChatButton.Visibility = vm.DeleteChatCommand?.CanExecute(null) == true
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            ApplyCaptions();
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            _captionsOpen = !_captionsOpen;
            ApplyCaptions();
        }

        private void ApplyCaptions()
        {
            if (CaptionRow != null)
            {
                CaptionRow.Visibility = _captionsOpen ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!_captionsOpen)
            {
                return;
            }

            if (AddContactCaption != null && AddContactButton != null)
            {
                AddContactCaption.Visibility = AddContactButton.Visibility;
            }

            if (DeleteCaption != null && DeleteChatButton != null)
            {
                DeleteCaption.Visibility = DeleteChatButton.Visibility;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (CloseCommand?.CanExecute(null) == true)
            {
                CloseCommand.Execute(null);
            }
        }

        private void AddContactButton_Click(object sender, RoutedEventArgs e)
        {
            var cmd = InfoViewModel?.AddContactCommand;
            if (cmd?.CanExecute(null) == true)
            {
                cmd.Execute(null);
            }
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            var cmd = InfoViewModel?.PinToStartCommand;
            if (cmd?.CanExecute(null) == true)
            {
                cmd.Execute(null);
            }
        }

        private void ChatPinButton_Click(object sender, RoutedEventArgs e)
        {
            var cmd = InfoViewModel?.PinChatCommand;
            if (cmd?.CanExecute(null) == true)
            {
                cmd.Execute(null);
            }
        }

        private void DeleteChatButton_Click(object sender, RoutedEventArgs e)
        {
            var cmd = InfoViewModel?.DeleteChatCommand;
            if (cmd?.CanExecute(null) == true)
            {
                cmd.Execute(null);
            }
        }
    }
}
