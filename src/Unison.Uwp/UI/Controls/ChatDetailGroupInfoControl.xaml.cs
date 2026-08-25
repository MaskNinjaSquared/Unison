using System.ComponentModel;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

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

            if (newVm != null)
            {
                ChatDetailInfoPivotHelper.ResetToRoot(InfoPivot);
            }

            ApplyInfoViewModel();
        }

        private void Info_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatDetailInfoViewModel.IsMembersAvatarsLoading))
            {
                ApplyMembersAvatarsLoading();
                return;
            }

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

            if (InfoPivot?.SelectedItem == MembersPivotItem && InfoViewModel != null)
            {
                _ = InfoViewModel.EnsureMembersAvatarsHydratedAsync();
                ApplyMembersAvatarsLoading();
            }
        }

        private void ApplyMembersAvatarsLoading()
        {
            if (MembersAvatarsProgress == null)
            {
                return;
            }

            bool loading = InfoViewModel?.IsMembersAvatarsLoading == true;
            MembersAvatarsProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        }

        private void NotificationsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            ChatDetailInfoPivotHelper.ExecuteNotificationsToggle(
                NotificationsToggle,
                InfoViewModel,
                _notificationsToggleQuiet);
        }

        private void MembersList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var member = e.ClickedItem as GroupMember;
            if (member == null)
            {
                return;
            }

            // Prefer walking to ChatDetailViewModel without referencing the view type (XamlPreCompile cycle).
            DependencyObject current = this;
            while (current != null)
            {
                var fe = current as FrameworkElement;
                var detailVm = fe?.DataContext as ChatDetailViewModel;
                if (detailVm != null)
                {
                    detailVm.OpenGroupMemberInfo(member);
                    return;
                }

                current = VisualTreeHelper.GetParent(current);
            }
        }

        private void ApplyInfoViewModel()
        {
            var vm = InfoViewModel;
            if (vm == null || ProfilePivotItem == null)
            {
                return;
            }

            if (MembersEmptyText != null)
            {
                MembersEmptyText.Visibility = vm.HasMembers ? Visibility.Collapsed : Visibility.Visible;
            }

            if (MembersList != null)
            {
                MembersList.ItemsSource = vm.HasMembers ? vm.Members : null;
                MembersList.Visibility = vm.HasMembers ? Visibility.Visible : Visibility.Collapsed;
            }

            MediaPane?.AttachPaging(vm, isFilesPane: false);
            FilesPane?.AttachPaging(vm);
            BindMediaPanes();

            if (InfoAvatar != null)
            {
                InfoAvatar.AvatarUrl = vm.AvatarUrl;
                InfoAvatar.IsGroup = true;
            }

            if (NameValue != null)
            {
                NameValue.Text = vm.DisplayName ?? string.Empty;
            }

            if (StatusValue != null)
            {
                StatusValue.Text = vm.HasStatusOrDescription ? vm.StatusOrDescription : "—";
            }

            ChatDetailInfoPivotHelper.ApplyNotificationsToggle(
                NotificationsToggle,
                vm,
                ref _notificationsToggleQuiet);

            if (MembersValue != null)
            {
                MembersValue.Text = vm.MembersCountText ?? "—";
            }

            ApplyMembersAvatarsLoading();
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
