// =============================================================================
// NewConversationViewModel.CreateFlow
//
// UI-only New Group / New Community wizards hosted inside the New Conversation
// overlay. Confirm shows a not-implemented dialog; no Socket / CreateGroupUseCase.
// =============================================================================
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public sealed partial class NewConversationViewModel
    {
        private NewConversationCreateFlow _createFlow = NewConversationCreateFlow.None;
        private int _groupStepIndex;
        private string _groupName = string.Empty;
        private string _communityName = string.Empty;
        private string _communityDescription = string.Empty;
        private bool _hasStartedCommunityCreate;
        private bool _isLayoutMinimal = true;
        private string _headerTitle = string.Empty;
        private string _primaryActionLabel = string.Empty;
        private string _primaryActionGlyph = "\uE72A";
        private string _createExplainTitle = string.Empty;
        private string _createExplainBody = string.Empty;
        private string _createExplainGlyph = "\uE716";

        private const string GlyphNext = "\uE72A";
        private const string GlyphConfirm = "\uE73E";
        private const string GlyphGroup = "\uE716";
        private const string GlyphGroupInfo = "\uE8F1";
        private const string GlyphCommunity = "\uE125";

        public ObservableCollection<ChatItem> SelectedMembers { get; } =
            new ObservableCollection<ChatItem>();

        public ICommand CreateNextCommand { get; private set; }

        public ICommand CreateBackCommand { get; private set; }

        public ICommand StartCommunityCreateCommand { get; private set; }

        public ICommand HeaderBackCommand { get; private set; }

        public NewConversationCreateFlow CreateFlow
        {
            get => _createFlow;
            private set
            {
                if (!Set(ref _createFlow, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsCreateFlowOpen));
                OnPropertyChanged(nameof(IsCreateGroup));
                OnPropertyChanged(nameof(IsCreateCommunity));
                OnPropertyChanged(nameof(ShowCommunityIntro));
                OnPropertyChanged(nameof(ShowCreateForm));
                OnPropertyChanged(nameof(ShowCreateExplainPane));
                RefreshCreateChrome();
            }
        }

        public bool IsCreateFlowOpen => _createFlow != NewConversationCreateFlow.None;

        public bool IsCreateGroup => _createFlow == NewConversationCreateFlow.Group;

        public bool IsCreateCommunity => _createFlow == NewConversationCreateFlow.Community;

        public int GroupStepIndex
        {
            get => _groupStepIndex;
            private set
            {
                if (!Set(ref _groupStepIndex, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsGroupMembersStep));
                OnPropertyChanged(nameof(IsGroupInfoStep));
                RefreshCreateChrome();
            }
        }

        public bool IsGroupMembersStep => IsCreateGroup && _groupStepIndex == 0;

        public bool IsGroupInfoStep => IsCreateGroup && _groupStepIndex == 1;

        public string GroupName
        {
            get => _groupName;
            set => Set(ref _groupName, value ?? string.Empty);
        }

        public string CommunityName
        {
            get => _communityName;
            set => Set(ref _communityName, value ?? string.Empty);
        }

        public string CommunityDescription
        {
            get => _communityDescription;
            set => Set(ref _communityDescription, value ?? string.Empty);
        }

        public bool HasStartedCommunityCreate
        {
            get => _hasStartedCommunityCreate;
            private set
            {
                if (!Set(ref _hasStartedCommunityCreate, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(ShowCommunityIntro));
                OnPropertyChanged(nameof(ShowCreateForm));
                OnPropertyChanged(nameof(ShowCreateExplainPane));
            }
        }

        /// <summary>Minimal + community + not started → intro cover (persists across layout flips once started).</summary>
        public bool ShowCommunityIntro =>
            IsCreateCommunity && _isLayoutMinimal && !_hasStartedCommunityCreate;

        /// <summary>Form column (group always; community after Começar or when not Minimal).</summary>
        public bool ShowCreateForm =>
            IsCreateGroup || (IsCreateCommunity && !ShowCommunityIntro);

        /// <summary>Right explainer + pane splitter (Extended+ only; same idea as chat list/detail).</summary>
        public bool ShowCreateExplainPane =>
            IsCreateFlowOpen && !_isLayoutMinimal && ShowCreateForm;

        public string HeaderTitle
        {
            get => _headerTitle;
            private set => Set(ref _headerTitle, value);
        }

        public string PrimaryActionLabel
        {
            get => _primaryActionLabel;
            private set => Set(ref _primaryActionLabel, value);
        }

        /// <summary>True on a Next step (Legacy bar shows the theme arrow asset instead of a glyph).</summary>
        public bool IsPrimaryActionNext => string.Equals(_primaryActionGlyph, GlyphNext, System.StringComparison.Ordinal);

        /// <summary>Segoe MDL2: forward on Next, accept on Confirm (circular FAB / Legacy bar).</summary>
        public string PrimaryActionGlyph
        {
            get => _primaryActionGlyph;
            private set
            {
                if (Set(ref _primaryActionGlyph, value))
                {
                    OnPropertyChanged(nameof(IsPrimaryActionNext));
                }
            }
        }

        public string CreateExplainTitle
        {
            get => _createExplainTitle;
            private set => Set(ref _createExplainTitle, value);
        }

        public string CreateExplainBody
        {
            get => _createExplainBody;
            private set => Set(ref _createExplainBody, value);
        }

        /// <summary>Empty-state icon for the Extended explain pane (same glyphs as New Chat options).</summary>
        public string CreateExplainGlyph
        {
            get => _createExplainGlyph;
            private set => Set(ref _createExplainGlyph, value);
        }

        /// <summary>Called from the view when LayoutStates Minimal / Extended changes.</summary>
        public void ReportLayoutMinimal(bool isMinimal)
        {
            if (_isLayoutMinimal == isMinimal)
            {
                return;
            }

            _isLayoutMinimal = isMinimal;
            OnPropertyChanged(nameof(ShowCommunityIntro));
            OnPropertyChanged(nameof(ShowCreateForm));
            OnPropertyChanged(nameof(ShowCreateExplainPane));
        }

        /// <summary>Consumes hardware/system back while a create flow is open.</summary>
        public bool TryHandleCreateBack()
        {
            if (_createFlow == NewConversationCreateFlow.None)
            {
                return false;
            }

            CreateBack();
            return true;
        }

        private void InitCreateFlowCommands()
        {
            CreateNextCommand = new RelayCommand(() => _ = CreateNextAsync());
            CreateBackCommand = new RelayCommand(CreateBack);
            StartCommunityCreateCommand = new RelayCommand(() => HasStartedCommunityCreate = true);
            HeaderBackCommand = new RelayCommand(HeaderBack);
            RefreshCreateChrome();
        }

        private void OpenCreateGroup()
        {
            ResetCreateFields();
            GroupStepIndex = 0;
            CreateFlow = NewConversationCreateFlow.Group;
        }

        private void OpenCreateCommunity()
        {
            ResetCreateFields();
            CommunityDescription = string.Empty;
            CreateFlow = NewConversationCreateFlow.Community;
        }

        private void ClearCreateFlow()
        {
            CreateFlow = NewConversationCreateFlow.None;
            ResetCreateFields();
            RefreshCreateChrome();
        }

        private void ResetCreateFields()
        {
            GroupStepIndex = 0;
            GroupName = string.Empty;
            CommunityName = string.Empty;
            CommunityDescription = string.Empty;
            HasStartedCommunityCreate = false;
            SelectedMembers.Clear();
        }

        private void HeaderBack()
        {
            if (_createFlow != NewConversationCreateFlow.None)
            {
                CreateBack();
                return;
            }

            Close();
        }

        private void CreateBack()
        {
            if (_createFlow == NewConversationCreateFlow.Group && _groupStepIndex > 0)
            {
                GroupStepIndex = 0;
                return;
            }

            ClearCreateFlow();
        }

        private async Task CreateNextAsync()
        {
            if (_createFlow == NewConversationCreateFlow.Group && _groupStepIndex == 0)
            {
                GroupStepIndex = 1;
                return;
            }

            await ShowNotImplementedAsync().ConfigureAwait(false);
        }

        private async Task ShowNotImplementedAsync()
        {
            string titleKey = _createFlow == NewConversationCreateFlow.Community
                ? "NewConversation_NewCommunity.Text"
                : "NewConversation_NewGroup.Text";
            string titleFallback = _createFlow == NewConversationCreateFlow.Community
                ? "New community"
                : "New group";
            await ShowComingSoonAsync(titleKey, titleFallback).ConfigureAwait(false);
        }

        private void RefreshCreateChrome()
        {
            if (_createFlow == NewConversationCreateFlow.None)
            {
                HeaderTitle = _strings?.Get("NewConversation_Title.Text", "New chat") ?? "New chat";
                PrimaryActionLabel = string.Empty;
                PrimaryActionGlyph = GlyphNext;
                CreateExplainGlyph = GlyphGroup;
                CreateExplainTitle = string.Empty;
                CreateExplainBody = string.Empty;
                return;
            }

            if (_createFlow == NewConversationCreateFlow.Group)
            {
                HeaderTitle = _strings?.Get("NewConversation_NewGroup.Text", "New group") ?? "New group";
                if (_groupStepIndex == 0)
                {
                    PrimaryActionLabel = _strings?.Get("CreateFlow_Next", "Next") ?? "Next";
                    PrimaryActionGlyph = GlyphNext;
                    CreateExplainGlyph = GlyphGroup;
                    CreateExplainTitle = _strings?.Get(
                        "CreateGroup_ExplainMembersTitle",
                        "Select members") ?? "Select members";
                    CreateExplainBody = _strings?.Get(
                        "CreateGroup_ExplainMembersBody",
                        "Choose the contacts that will be part of this group.")
                        ?? "Choose the contacts that will be part of this group.";
                }
                else
                {
                    PrimaryActionLabel = _strings?.Get("CreateFlow_Confirm", "Confirm") ?? "Confirm";
                    PrimaryActionGlyph = GlyphConfirm;
                    CreateExplainGlyph = GlyphGroupInfo;
                    CreateExplainTitle = _strings?.Get(
                        "CreateGroup_ExplainInfoTitle",
                        "Group details") ?? "Group details";
                    CreateExplainBody = _strings?.Get(
                        "CreateGroup_ExplainInfoBody",
                        "Give your group a name and review the details before creating it.")
                        ?? "Give your group a name and review the details before creating it.";
                }

                return;
            }

            HeaderTitle = _strings?.Get("NewConversation_NewCommunity.Text", "New community")
                ?? "New community";
            PrimaryActionLabel = _strings?.Get("CreateFlow_Confirm", "Confirm") ?? "Confirm";
            PrimaryActionGlyph = GlyphConfirm;
            CreateExplainGlyph = GlyphCommunity;
            CreateExplainTitle = _strings?.Get(
                "CreateCommunity_ExplainTitle",
                "Create community") ?? "Create community";
            CreateExplainBody = _strings?.Get(
                "CreateCommunity_ExplainBody",
                "Bring together people from your neighborhood, your school, and more in one place. Create groups for specific topics and send announcements to everyone easily.")
                ?? "Bring together people from your neighborhood, your school, and more in one place. Create groups for specific topics and send announcements to everyone easily.";
        }
    }
}
