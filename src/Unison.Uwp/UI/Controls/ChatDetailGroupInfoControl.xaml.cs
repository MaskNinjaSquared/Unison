using System.ComponentModel;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class ChatDetailGroupInfoControl : UserControl
    {
        public static readonly DependencyProperty InfoViewModelProperty =
            DependencyProperty.Register(
                nameof(InfoViewModel),
                typeof(ChatDetailInfoViewModel),
                typeof(ChatDetailGroupInfoControl),
                new PropertyMetadata(null, OnInfoViewModelChanged));

        private ChatDetailInfoViewModel _boundInfo;
        private bool _notificationsToggleQuiet;

        public ChatDetailGroupInfoControl()
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
            var control = d as ChatDetailGroupInfoControl;
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

            ProfilePivotItem.Header = ChatDetailInfoPivotHelper.Upper(vm.ProfilePivotHeader);
            if (MembersPivotItem != null)
            {
                MembersPivotItem.Header = ChatDetailInfoPivotHelper.Upper(vm.MembersPivotHeader);
            }

            if (MediaPivotItem != null)
            {
                MediaPivotItem.Header = ChatDetailInfoPivotHelper.Upper(vm.MediaPivotHeader);
            }

            if (FilesPivotItem != null)
            {
                FilesPivotItem.Header = ChatDetailInfoPivotHelper.Upper(vm.FilesPivotHeader);
            }

            if (MembersEmptyText != null)
            {
                MembersEmptyText.Text = vm.MembersEmptyText ?? string.Empty;
            }

            MediaPane?.Bind(vm.MediaItems, vm.HasMedia, vm.MediaEmptyText);
            FilesPane?.Bind(vm.FileItems, vm.HasFiles, vm.FilesEmptyText);

            if (InfoAvatar != null)
            {
                InfoAvatar.AvatarUrl = vm.AvatarUrl;
                InfoAvatar.IsGroup = true;
            }

            if (NameLabel != null)
            {
                NameLabel.Text = ChatDetailInfoPivotHelper.Upper(vm.NameSectionLabel);
            }

            if (NameValue != null)
            {
                NameValue.Text = vm.DisplayName ?? string.Empty;
            }

            if (StatusLabel != null)
            {
                StatusLabel.Text = ChatDetailInfoPivotHelper.Upper(vm.StatusSectionLabel);
            }

            if (StatusValue != null)
            {
                StatusValue.Text = vm.HasStatusOrDescription ? vm.StatusOrDescription : "—";
            }

            if (NotificationsLabel != null)
            {
                NotificationsLabel.Text = ChatDetailInfoPivotHelper.Upper(vm.NotificationsSectionLabel);
            }

            ChatDetailInfoPivotHelper.ApplyNotificationsToggle(
                NotificationsToggle,
                vm,
                ref _notificationsToggleQuiet);

            if (MembersLabel != null)
            {
                MembersLabel.Text = ChatDetailInfoPivotHelper.Upper(vm.MembersSectionLabel);
            }

            if (MembersValue != null)
            {
                MembersValue.Text = vm.MembersCountText ?? "—";
            }
        }
    }
}
