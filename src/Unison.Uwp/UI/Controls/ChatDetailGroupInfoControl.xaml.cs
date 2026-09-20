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
        private bool _infoHooked;
        private bool _isLoaded;
        private bool _notificationsToggleQuiet;

        public ChatDetailGroupInfoControl()
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

        private static void OnInfoViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ChatDetailGroupInfoControl;
            control?.OnInfoViewModelChanged(e.OldValue as ChatDetailInfoViewModel, e.NewValue as ChatDetailInfoViewModel);
        }

        private void OnInfoViewModelChanged(ChatDetailInfoViewModel oldVm, ChatDetailInfoViewModel newVm)
        {
            UnhookInfo();
            _boundInfo = newVm;
            HookInfo();

            if (newVm != null)
            {
                ChatDetailInfoPivotHelper.ResetToRoot(InfoPivot);
            }

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
            if (e.PropertyName == nameof(ChatDetailInfoViewModel.IsMembersAvatarsLoading) ||
                e.PropertyName == nameof(ChatDetailInfoViewModel.IsMembersRosterLoading))
            {
                ApplyMembersLoadingUi();
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
                _ = InfoViewModel.EnsureMembersPivotReadyAsync();
                ApplyMembersLoadingUi();
            }
        }

        private void ApplyMembersLoadingUi()
        {
            var vm = InfoViewModel;
            bool rosterLoading = vm?.IsMembersRosterLoading == true;
            bool avatarsLoading = vm?.IsMembersAvatarsLoading == true;

            if (MembersRosterLoadingRing != null)
            {
                MembersRosterLoadingRing.IsActive = rosterLoading;
                MembersRosterLoadingRing.Visibility =
                    rosterLoading ? Visibility.Visible : Visibility.Collapsed;
            }

            if (MembersAvatarsProgress != null)
            {
                MembersAvatarsProgress.Visibility =
                    avatarsLoading && !rosterLoading ? Visibility.Visible : Visibility.Collapsed;
            }

            // Empty / list while roster IQ is in flight — avoid a false "no members" flash.
            if (rosterLoading)
            {
                if (MembersEmptyText != null)
                {
                    MembersEmptyText.Visibility = Visibility.Collapsed;
                }

                if (MembersList != null)
                {
                    MembersList.Visibility = Visibility.Collapsed;
                }
            }
            else if (vm != null)
            {
                ApplyMembersListVisibility(vm);
            }
        }

        private void ApplyMembersListVisibility(ChatDetailInfoViewModel vm)
        {
            if (MembersEmptyText != null)
            {
                MembersEmptyText.Visibility = vm.HasMembers ? Visibility.Collapsed : Visibility.Visible;
            }

            if (MembersList != null)
            {
                MembersList.ItemsSource = vm.HasMembers ? vm.Members : null;
                MembersList.Visibility = vm.HasMembers ? Visibility.Visible : Visibility.Collapsed;
            }
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

            if (vm.IsMembersRosterLoading)
            {
                if (MembersEmptyText != null)
                {
                    MembersEmptyText.Visibility = Visibility.Collapsed;
                }

                if (MembersList != null)
                {
                    MembersList.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                ApplyMembersListVisibility(vm);
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

            ApplyMembersLoadingUi();
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
