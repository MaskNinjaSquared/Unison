using System.ComponentModel;
using System.Windows.Input;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Chat-side info pane shell for 1:1 user and group info only.
    /// Group-member profile uses <see cref="ChatDetailGroupMemberInfoPane"/> (separate control).
    /// </summary>
    public sealed partial class ChatDetailInfoControl : UserControl
    {
        public static readonly DependencyProperty InfoViewModelProperty =
            DependencyProperty.Register(
                nameof(InfoViewModel),
                typeof(ChatDetailInfoViewModel),
                typeof(ChatDetailInfoControl),
                new PropertyMetadata(null, OnInfoViewModelChanged));

        public static readonly DependencyProperty CloseCommandProperty =
            DependencyProperty.Register(
                nameof(CloseCommand),
                typeof(ICommand),
                typeof(ChatDetailInfoControl),
                new PropertyMetadata(null));

        private ChatDetailInfoViewModel _boundInfo;
        private ChatDetailUserInfoControl _userInfoHost;
        private ChatDetailGroupInfoControl _groupInfoHost;

        public ChatDetailInfoControl()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyInfoViewModel();
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
            var control = d as ChatDetailInfoControl;
            control?.OnInfoViewModelChanged(e.OldValue as ChatDetailInfoViewModel, e.NewValue as ChatDetailInfoViewModel);
        }

        private void OnInfoViewModelChanged(ChatDetailInfoViewModel oldVm, ChatDetailInfoViewModel newVm)
        {
            if (_boundInfo != null)
            {
                _boundInfo.PropertyChanged -= Info_PropertyChanged;
            }

            _boundInfo = newVm;
            if (_boundInfo != null)
            {
                _boundInfo.PropertyChanged += Info_PropertyChanged;
            }

            ApplyInfoViewModel();
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

            // Avoid generated UserControl fields (XamlPreCompile omits them). Walk the body grid instead.
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
                if (canAdd)
                {
                    string addLabel = vm.AddContactAppBarLabel ?? "Add\ncontact";
                    if (AddContactButtonLabel != null)
                    {
                        AddContactButtonLabel.Text = addLabel;
                    }

                    ToolTipService.SetToolTip(
                        AddContactButton,
                        addLabel.Replace('\n', ' ').Replace("  ", " ").Trim());
                    AddContactButton.IsEnabled = vm.AddContactCommand?.CanExecute(null) == true;
                }
            }

            if (PinButton != null)
            {
                string pinLabel = vm.PinMenuLabel ?? "Pin to\nStart";
                if (PinButtonLabel != null)
                {
                    PinButtonLabel.Text = pinLabel;
                }

                string tip = pinLabel.Replace('\n', ' ').Replace("  ", " ").Trim();
                ToolTipService.SetToolTip(PinButton, tip);
                PinButton.IsEnabled = vm.PinToStartCommand?.CanExecute(null) == true;
            }

            if (ChatPinButton != null)
            {
                string chatPinLabel = vm.ChatPinLabel ?? "Pin\nchat";
                if (ChatPinButtonLabel != null)
                {
                    ChatPinButtonLabel.Text = chatPinLabel;
                }

                ToolTipService.SetToolTip(ChatPinButton, chatPinLabel.Replace('\n', ' ').Replace("  ", " ").Trim());
                ChatPinButton.IsEnabled = vm.PinChatCommand?.CanExecute(null) == true;
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
    }
}
