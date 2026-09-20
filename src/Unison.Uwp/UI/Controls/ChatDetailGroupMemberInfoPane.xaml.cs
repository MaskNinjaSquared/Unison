using System.ComponentModel;
using System.Windows.Input;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Separate shell for group-member info (avoids sharing <see cref="ChatDetailInfoControl"/> hosts).
    /// </summary>
    public sealed partial class ChatDetailGroupMemberInfoPane : UserControl
    {
        public static readonly DependencyProperty InfoViewModelProperty =
            DependencyProperty.Register(
                nameof(InfoViewModel),
                typeof(ChatDetailInfoViewModel),
                typeof(ChatDetailGroupMemberInfoPane),
                new PropertyMetadata(null, OnInfoViewModelChanged));

        public static readonly DependencyProperty CloseCommandProperty =
            DependencyProperty.Register(
                nameof(CloseCommand),
                typeof(ICommand),
                typeof(ChatDetailGroupMemberInfoPane),
                new PropertyMetadata(null));

        private ChatDetailInfoViewModel _boundInfo;
        private bool _infoHooked;
        private bool _isLoaded;

        public ChatDetailGroupMemberInfoPane()
        {
            InitializeComponent();
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
            var control = d as ChatDetailGroupMemberInfoPane;
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

        /// <summary>
        /// Listens only while on screen. The view model outlives this control, so a subscription
        /// left behind at unload keeps the whole visual tree alive and goes on laying out a pane
        /// nobody is looking at.
        /// </summary>
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

        private void ApplyInfoViewModel()
        {
            if (MemberInfoHost == null)
            {
                return;
            }

            var vm = InfoViewModel;
            MemberInfoHost.InfoViewModel = vm != null && vm.IsGroupMember ? vm : null;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (CloseCommand?.CanExecute(null) == true)
            {
                CloseCommand.Execute(null);
            }
        }
    }
}
