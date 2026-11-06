using System;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Unison / WhatsApp scaffold: create group (SemanticZoom members + FAB) / community.
    /// </summary>
    public sealed partial class CreateConversationFlowView : UserControl
    {
        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register(
                nameof(ViewModel),
                typeof(NewConversationViewModel),
                typeof(CreateConversationFlowView),
                new PropertyMetadata(null, OnViewModelChanged));

        private NewConversationViewModel _hookedVm;
        private AlphabetSemanticZoomHost _membersZoom;
        private PaneSplitterHost _splitter;
        private bool _suppressPivotSync;
        private RichLabeledTextBox _emojiTarget;
        private bool _groupPhotoButtonHooked;

        public CreateConversationFlowView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public NewConversationViewModel ViewModel
        {
            get => (NewConversationViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as CreateConversationFlowView;
            control?.AttachViewModel(
                e.OldValue as NewConversationViewModel,
                e.NewValue as NewConversationViewModel);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureMembersZoom();
            HookGroupPhotoButton();
            ApplyExplainColumns();
            BindMembers();
            SyncPivotFromViewModel();
            _ = ApplyCommunityPhotoPreviewAsync();
            _ = ApplyGroupPhotoPreviewAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _membersZoom?.Clear();
            UnhookGroupPhotoButton();
            DetachViewModel();
        }

        private void AttachViewModel(NewConversationViewModel oldVm, NewConversationViewModel newVm)
        {
            DetachViewModel();
            LayoutRoot.DataContext = newVm;
            _hookedVm = newVm;
            if (_hookedVm != null)
            {
                _hookedVm.PropertyChanged += ViewModel_PropertyChanged;
            }

            EnsureMembersZoom();
            HookGroupPhotoButton();
            ApplyExplainColumns();
            BindMembers();
            SyncPivotFromViewModel();
            _ = ApplyCommunityPhotoPreviewAsync();
            _ = ApplyGroupPhotoPreviewAsync();
        }

        private void DetachViewModel()
        {
            if (_hookedVm != null)
            {
                _hookedVm.PropertyChanged -= ViewModel_PropertyChanged;
                _hookedVm = null;
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || ViewModel == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.GroupedContacts), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(NewConversationViewModel.JumpTiles), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(NewConversationViewModel.IsCreateGroup), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(NewConversationViewModel.ShowCreateForm), StringComparison.Ordinal))
            {
                BindMembers();
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.GroupStepIndex), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(NewConversationViewModel.IsCreateGroup), StringComparison.Ordinal))
            {
                SyncPivotFromViewModel();
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.ShowCreateExplainPane), StringComparison.Ordinal))
            {
                ApplyExplainColumns();
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.CommunityPhotoBytes), StringComparison.Ordinal))
            {
                _ = ApplyCommunityPhotoPreviewAsync();
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.GroupPhotoBytes), StringComparison.Ordinal))
            {
                _ = ApplyGroupPhotoPreviewAsync();
            }
        }

        private void EnsureMembersZoom()
        {
            if (_membersZoom != null || MembersZoom == null || CreateMembersList == null || MembersJumpList == null)
            {
                return;
            }

            _membersZoom = new AlphabetSemanticZoomHost(MembersZoom, CreateMembersList, MembersJumpList);
        }

        private void BindMembers()
        {
            EnsureMembersZoom();
            if (_membersZoom == null || ViewModel == null || !ViewModel.IsCreateGroup)
            {
                return;
            }

            _membersZoom.Bind(ViewModel.GroupedContacts, ViewModel.JumpTiles);
            _membersZoom.ResetZoomedIn();
        }

        private void ApplyExplainColumns()
        {
            if (_splitter == null)
            {
                _splitter = new PaneSplitterHost(
                    CreateSplitter,
                    CreateSplitterChrome,
                    LayoutRoot,
                    CreateFormColumn,
                    CreateExplainColumn);
            }

            _splitter.SetTwoPane(ViewModel != null && ViewModel.ShowCreateExplainPane);
        }

        private void SyncPivotFromViewModel()
        {
            if (CreateGroupPivot == null || ViewModel == null || !ViewModel.IsCreateGroup)
            {
                return;
            }

            int target = ViewModel.GroupStepIndex;
            if (CreateGroupPivot.SelectedIndex == target)
            {
                return;
            }

            _suppressPivotSync = true;
            try
            {
                CreateGroupPivot.SelectedIndex = target;
            }
            finally
            {
                _suppressPivotSync = false;
            }
        }

        private void CreateGroupPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPivotSync || ViewModel == null || !ViewModel.IsCreateGroup)
            {
                return;
            }

            int index = CreateGroupPivot.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            while (ViewModel.GroupStepIndex < index)
            {
                ViewModel.CreateNextCommand.Execute(null);
            }

            while (ViewModel.GroupStepIndex > index)
            {
                ViewModel.TryHandleCreateBack();
            }
        }

        private void CreateMembersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel == null || CreateMembersList == null)
            {
                return;
            }

            ViewModel.SelectedMembers.Clear();
            foreach (object item in CreateMembersList.SelectedItems)
            {
                if (item is ChatItem chat)
                {
                    ViewModel.SelectedMembers.Add(chat);
                }
            }
        }

        /// <summary>Consumes back while members SemanticZoom is zoomed out.</summary>
        public bool TryHandleMembersZoomBack()
        {
            if (ChannelFormOverlay != null && ChannelFormOverlay.TryHandleBack())
            {
                return true;
            }

            if (TryCloseEmojiDock(CommunityEmojiDock, CommunityEmojiPanel) ||
                TryCloseEmojiDock(GroupEmojiDock, GroupEmojiPanel))
            {
                return true;
            }

            return _membersZoom != null && _membersZoom.TryHandleBackToZoomedIn();
        }

        private bool TryCloseEmojiDock(Border dock, EmojiPickerPanel panel)
        {
            if (dock == null || dock.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (panel != null && panel.TryConsumeBack())
            {
                return true;
            }

            dock.Visibility = Visibility.Collapsed;
            _emojiTarget = null;
            return true;
        }

        private void CommunityField_EmojiInvoked(object sender, EventArgs e) =>
            ToggleEmojiDock(sender as RichLabeledTextBox, CommunityEmojiDock, EnsureCommunityEmojiCatalog);

        private void GroupField_EmojiInvoked(object sender, EventArgs e) =>
            ToggleEmojiDock(sender as RichLabeledTextBox, GroupEmojiDock, EnsureGroupEmojiCatalog);

        private void ToggleEmojiDock(
            RichLabeledTextBox field,
            Border dock,
            Action ensureCatalog)
        {
            if (field == null || dock == null)
            {
                return;
            }

            bool sameTarget = ReferenceEquals(_emojiTarget, field);
            bool isOpen = dock.Visibility == Visibility.Visible;
            if (isOpen && sameTarget)
            {
                dock.Visibility = Visibility.Collapsed;
                _emojiTarget = null;
                return;
            }

            // Only one emoji dock open at a time.
            if (CommunityEmojiDock != null)
            {
                CommunityEmojiDock.Visibility = Visibility.Collapsed;
            }

            if (GroupEmojiDock != null)
            {
                GroupEmojiDock.Visibility = Visibility.Collapsed;
            }

            _emojiTarget = field;
            ensureCatalog?.Invoke();
            dock.Visibility = Visibility.Visible;
            field.FocusInput();
        }

        private void CommunityEmojiPanel_GlyphPicked(object sender, string glyph) =>
            InsertEmojiIntoTarget(glyph);

        private void GroupEmojiPanel_GlyphPicked(object sender, string glyph) =>
            InsertEmojiIntoTarget(glyph);

        private void InsertEmojiIntoTarget(string glyph)
        {
            if (_emojiTarget == null || string.IsNullOrEmpty(glyph))
            {
                return;
            }

            _emojiTarget.InsertEmoji(glyph);
        }

        private void HookGroupPhotoButton()
        {
            if (_groupPhotoButtonHooked || GroupPhotoButton == null)
            {
                return;
            }

            GroupPhotoButton.CameraRequested += GroupPhotoButton_PickRequested;
            GroupPhotoButton.GalleryRequested += GroupPhotoButton_PickRequested;
            GroupPhotoButton.DeleteRequested += GroupPhotoButton_DeleteRequested;
            _groupPhotoButtonHooked = true;
        }

        private void UnhookGroupPhotoButton()
        {
            if (!_groupPhotoButtonHooked || GroupPhotoButton == null)
            {
                return;
            }

            GroupPhotoButton.CameraRequested -= GroupPhotoButton_PickRequested;
            GroupPhotoButton.GalleryRequested -= GroupPhotoButton_PickRequested;
            GroupPhotoButton.DeleteRequested -= GroupPhotoButton_DeleteRequested;
            _groupPhotoButtonHooked = false;
        }

        private void GroupPhotoButton_PickRequested(object sender, EventArgs e)
        {
            if (ViewModel?.PickGroupPhotoCommand != null &&
                ViewModel.PickGroupPhotoCommand.CanExecute(null))
            {
                ViewModel.PickGroupPhotoCommand.Execute(null);
            }
        }

        private void GroupPhotoButton_DeleteRequested(object sender, EventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.GroupPhotoBytes = null;
            }
        }

        private void EnsureCommunityEmojiCatalog() =>
            EnsureEmojiCatalog(CommunityEmojiPanel);

        private void EnsureGroupEmojiCatalog() =>
            EnsureEmojiCatalog(GroupEmojiPanel);

        private static void EnsureEmojiCatalog(EmojiPickerPanel panel)
        {
            if (panel == null || panel.CategoriesSource != null)
            {
                return;
            }

            panel.CategoriesSource = EmojiCatalog.BuiltInCategoriesFor(DeviceCategory.High);
        }

        private System.Threading.Tasks.Task ApplyCommunityPhotoPreviewAsync() =>
            ApplyPhotoBrushAsync(CommunityPhotoBrush, ViewModel?.CommunityPhotoBytes);

        private System.Threading.Tasks.Task ApplyGroupPhotoPreviewAsync() =>
            ApplyPhotoBrushAsync(GroupPhotoBrush, ViewModel?.GroupPhotoBytes);

        private static async System.Threading.Tasks.Task ApplyPhotoBrushAsync(
            ImageBrush brush,
            byte[] bytes)
        {
            if (brush == null)
            {
                return;
            }

            if (bytes == null || bytes.Length == 0)
            {
                brush.ImageSource = null;
                return;
            }

            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 320 };
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);
                    await bitmap.SetSourceAsync(stream);
                }

                brush.ImageSource = bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[CreateConversationFlow] Photo preview: " + ex.Message);
                brush.ImageSource = null;
            }
        }
    }
}
