using System.ComponentModel;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Group-member info pane: profile + groups in common (outer scroll) + media/files filtered by author.
    /// </summary>
    public sealed partial class ChatDetailGroupMemberInfoControl : UserControl
    {
        public static readonly DependencyProperty InfoViewModelProperty =
            DependencyProperty.Register(
                nameof(InfoViewModel),
                typeof(ChatDetailInfoViewModel),
                typeof(ChatDetailGroupMemberInfoControl),
                new PropertyMetadata(null, OnInfoViewModelChanged));

        private ChatDetailInfoViewModel _boundInfo;

        public ChatDetailGroupMemberInfoControl()
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
            var control = d as ChatDetailGroupMemberInfoControl;
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

        private void ApplyInfoViewModel()
        {
            var vm = InfoViewModel;
            if (vm == null || ProfilePivotItem == null)
            {
                return;
            }

            ProfilePivotItem.Header = ChatDetailInfoPivotHelper.Upper(vm.ProfilePivotHeader);
            if (MediaPivotItem != null)
            {
                MediaPivotItem.Header = ChatDetailInfoPivotHelper.Upper(vm.MediaPivotHeader);
            }

            if (FilesPivotItem != null)
            {
                FilesPivotItem.Header = ChatDetailInfoPivotHelper.Upper(vm.FilesPivotHeader);
            }

            MediaPane?.AttachPaging(vm, isFilesPane: false);
            FilesPane?.AttachPaging(vm);
            BindMediaPanes();

            if (InfoAvatar != null)
            {
                InfoAvatar.AvatarUrl = vm.AvatarUrl;
                InfoAvatar.IsGroup = false;
            }

            if (NameLabel != null)
            {
                NameLabel.Text = ChatDetailInfoPivotHelper.Upper(vm.NameSectionLabel);
            }

            if (NameValue != null)
            {
                NameValue.Text = vm.DisplayName ?? string.Empty;
            }

            if (AdminValue != null)
            {
                AdminValue.Text = vm.AdminRoleText ?? string.Empty;
                AdminValue.Visibility = vm.IsMemberAdmin ? Visibility.Visible : Visibility.Collapsed;
            }

            if (PhoneSection != null)
            {
                PhoneSection.Visibility = (vm.HasPhone || vm.CanAddToAddressBook)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (PhoneLabel != null)
            {
                PhoneLabel.Text = ChatDetailInfoPivotHelper.Upper(vm.PhoneSectionLabel);
            }

            if (PhoneValue != null)
            {
                PhoneValue.Text = vm.PhoneValue ?? string.Empty;
            }

            if (AddContactButton != null)
            {
                AddContactButton.Content = vm.AddContactLabel;
                AddContactButton.Command = vm.AddContactCommand;
                AddContactButton.Visibility = vm.CanAddToAddressBook
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (SharedGroupsLabel != null)
            {
                SharedGroupsLabel.Text = ChatDetailInfoPivotHelper.Upper(vm.SharedGroupsSectionLabel);
            }

            if (SharedGroupsList != null)
            {
                SharedGroupsList.ItemsSource = vm.SharedGroups;
                SharedGroupsList.Visibility = vm.HasSharedGroups ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SharedGroupsEmpty != null)
            {
                SharedGroupsEmpty.Text = vm.SharedGroupsEmptyText ?? string.Empty;
                SharedGroupsEmpty.Visibility = vm.HasSharedGroups ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void BindMediaPanes()
        {
            var vm = InfoViewModel;
            if (vm == null)
            {
                return;
            }

            bool loading = vm.IsMediaIndexLoading;
            MediaPane?.Bind(vm.MediaItems, vm.HasMedia, vm.MediaEmptyText, loading);
            FilesPane?.Bind(vm.FileItems, vm.HasFiles, vm.FilesEmptyText, loading);
        }
    }
}
