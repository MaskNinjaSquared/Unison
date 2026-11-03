using System;
using System.ComponentModel;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Unison.Uwp.Shell.WhatsAppLegacy.Views;
using Unison.Uwp.UI.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Controls
{
    /// <summary>
    /// WhatsApp Legacy scaffold: create group (SemanticZoom members + CommandBar) / community.
    /// Same content as the Unison/WhatsApp flow; chrome only differs.
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
            ApplyExplainColumns();
            BindMembers();
            SyncPivotFromViewModel();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _membersZoom?.Clear();
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
            ApplyExplainColumns();
            BindMembers();
            SyncPivotFromViewModel();
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

        private void CreateLegacyCommandBar_Loaded(object sender, RoutedEventArgs e)
        {
            LegacyCommandBarLayout.CenterPrimaryCommands(sender as CommandBar);
        }

        public bool TryHandleMembersZoomBack()
        {
            return _membersZoom != null && _membersZoom.TryHandleBackToZoomedIn();
        }
    }
}
