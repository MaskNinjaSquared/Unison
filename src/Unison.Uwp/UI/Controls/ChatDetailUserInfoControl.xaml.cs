using System.ComponentModel;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class ChatDetailUserInfoControl : UserControl
    {
        public static readonly DependencyProperty InfoViewModelProperty =
            DependencyProperty.Register(
                nameof(InfoViewModel),
                typeof(ChatDetailInfoViewModel),
                typeof(ChatDetailUserInfoControl),
                new PropertyMetadata(null, OnInfoViewModelChanged));

        private ChatDetailInfoViewModel _boundInfo;
        private bool _notificationsToggleQuiet;

        public ChatDetailUserInfoControl()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyInfoViewModel();
        }

        public ChatDetailInfoViewModel InfoViewModel
        {
            get { return (ChatDetailInfoViewModel)GetValue(InfoViewModelProperty); }
            set { SetValue(InfoViewModelProperty, value); }
        }

        private static void OnInfoViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ChatDetailUserInfoControl;
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

            if (newVm != null)
            {
                ChatDetailInfoPivotHelper.ResetToRoot(InfoPivot);
            }

            ApplyInfoViewModel();
        }

        private void Info_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (ChatDetailInfoPivotHelper.IsMediaPaneProperty(e.PropertyName))
            {
                BindMediaPanes();
                return;
            }

            ApplyInfoViewModel();
        }

        private void InfoPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ChatDetailInfoPivotHelper.RequestMediaIndexIfSelected(
                InfoPivot,
                InfoViewModel,
                MediaPivotItem,
                FilesPivotItem);
        }

        private void NotificationsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            ChatDetailInfoPivotHelper.ExecuteNotificationsToggle(
                NotificationsToggle,
                InfoViewModel,
                _notificationsToggleQuiet);
        }

        private void ApplyInfoViewModel()
        {
            var vm = InfoViewModel;
            if (vm == null || ProfilePivotItem == null)
            {
                return;
            }

            MediaPane?.AttachPaging(vm, isFilesPane: false);
            FilesPane?.AttachPaging(vm);
            BindMediaPanes();

            if (InfoAvatar != null)
            {
                InfoAvatar.AvatarUrl = vm.AvatarUrl;
                InfoAvatar.IsGroup = false;
            }

            if (NameValue != null)
            {
                NameValue.Text = vm.DisplayName ?? string.Empty;
            }

            if (PhoneSection != null)
            {
                PhoneSection.Visibility = (vm.HasPhone || vm.CanAddToAddressBook)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (PhoneValue != null)
            {
                PhoneValue.Text = string.IsNullOrWhiteSpace(vm.PhoneValue) ? "—" : vm.PhoneValue;
            }

            if (AddContactButton != null)
            {
                AddContactButton.Command = vm.AddContactCommand;
                AddContactButton.Visibility = vm.CanAddToAddressBook
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (StatusValue != null)
            {
                StatusValue.Text = vm.HasStatusOrDescription ? vm.StatusOrDescription : "—";
            }

            ChatDetailInfoPivotHelper.ApplyNotificationsToggle(
                NotificationsToggle,
                vm,
                ref _notificationsToggleQuiet);
        }

        private void BindMediaPanes()
        {
            var vm = InfoViewModel;
            if (vm == null)
            {
                return;
            }

            bool loading = vm.IsMediaIndexLoading;
            MediaPane?.Bind(vm.MediaItems, vm.HasMedia, loading);
            FilesPane?.Bind(vm.FileItems, vm.HasFiles, loading);
        }
    }
}
