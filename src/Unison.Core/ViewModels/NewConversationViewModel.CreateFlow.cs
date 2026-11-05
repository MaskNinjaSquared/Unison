// =============================================================================
// NewConversationViewModel.CreateFlow
//
// UI-only New Group / New Community wizards hosted inside the New Conversation
// overlay. Confirm shows a not-implemented dialog; no Socket / CreateGroupUseCase.
// =============================================================================
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        private byte[] _groupPhotoBytes;
        private GroupEphemeralDuration _groupEphemeralDuration = GroupEphemeralDuration.Off;
        private bool _isGroupPermissionsOpen;
        private string _communityName = string.Empty;
        private string _communityDescription = string.Empty;
        private byte[] _communityPhotoBytes;
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

        /// <summary>Opens the image picker and stores optimized JPEG/PNG bytes for the community photo.</summary>
        public ICommand PickCommunityPhotoCommand { get; private set; }

        /// <summary>Opens the image picker for the create-group avatar.</summary>
        public ICommand PickGroupPhotoCommand { get; private set; }

        /// <summary>Opens the disappearing-messages dialog for create-group.</summary>
        public ICommand EditGroupEphemeralCommand { get; private set; }

        /// <summary>Shows the (stub) group-permissions overlay.</summary>
        public ICommand OpenGroupPermissionsCommand { get; private set; }

        /// <summary>Closes the group-permissions overlay.</summary>
        public ICommand CloseGroupPermissionsCommand { get; private set; }

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
                OnPropertyChanged(nameof(ShowCreateActionChrome));
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

        /// <summary>Picked group avatar bytes (null when none).</summary>
        public byte[] GroupPhotoBytes
        {
            get => _groupPhotoBytes;
            set
            {
                if (ReferenceEquals(_groupPhotoBytes, value))
                {
                    return;
                }

                _groupPhotoBytes = value;
                OnPropertyChanged(nameof(GroupPhotoBytes));
                OnPropertyChanged(nameof(HasGroupPhoto));
            }
        }

        public bool HasGroupPhoto =>
            _groupPhotoBytes != null && _groupPhotoBytes.Length > 0;

        public GroupEphemeralDuration GroupEphemeralDuration
        {
            get => _groupEphemeralDuration;
            set
            {
                if (!Set(ref _groupEphemeralDuration, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(GroupEphemeralStatusLabel));
            }
        }

        /// <summary>Localized status under the temporary-messages row (Off / 24h / 7d / 90d).</summary>
        public string GroupEphemeralStatusLabel => FormatEphemeralLabel(_groupEphemeralDuration);

        public bool IsGroupPermissionsOpen
        {
            get => _isGroupPermissionsOpen;
            private set
            {
                if (!Set(ref _isGroupPermissionsOpen, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(ShowCreateFlowHeader));
                OnPropertyChanged(nameof(ShowCreateActionChrome));
            }
        }

        /// <summary>New group / community header — collapsed while the permissions overlay owns back + title.</summary>
        public bool ShowCreateFlowHeader => !_isGroupPermissionsOpen;

        /// <summary>FAB / Legacy command bar — hidden under the permissions overlay.</summary>
        public bool ShowCreateActionChrome => ShowCreateForm && !_isGroupPermissionsOpen;

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

        /// <summary>Picked community avatar bytes (null when none). Cleared when leaving the flow.</summary>
        public byte[] CommunityPhotoBytes
        {
            get => _communityPhotoBytes;
            set
            {
                if (ReferenceEquals(_communityPhotoBytes, value))
                {
                    return;
                }

                _communityPhotoBytes = value;
                OnPropertyChanged(nameof(CommunityPhotoBytes));
                OnPropertyChanged(nameof(HasCommunityPhoto));
            }
        }

        public bool HasCommunityPhoto =>
            _communityPhotoBytes != null && _communityPhotoBytes.Length > 0;

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
                OnPropertyChanged(nameof(ShowCreateActionChrome));
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
            OnPropertyChanged(nameof(ShowCreateActionChrome));
        }

        /// <summary>Consumes hardware/system back while a create flow is open.</summary>
        public bool TryHandleCreateBack()
        {
            if (_createFlow == NewConversationCreateFlow.None)
            {
                return false;
            }

            if (_isGroupPermissionsOpen)
            {
                IsGroupPermissionsOpen = false;
                return true;
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
            PickCommunityPhotoCommand = new RelayCommand(
                () => _ = PickCommunityPhotoAsync(),
                () => _filePicker != null);
            PickGroupPhotoCommand = new RelayCommand(
                () => _ = PickGroupPhotoAsync(),
                () => _filePicker != null);
            EditGroupEphemeralCommand = new RelayCommand(() => _ = EditGroupEphemeralAsync());
            OpenGroupPermissionsCommand = new RelayCommand(() => IsGroupPermissionsOpen = true);
            CloseGroupPermissionsCommand = new RelayCommand(() => IsGroupPermissionsOpen = false);
            RefreshCreateChrome();
        }

        private async Task PickCommunityPhotoAsync()
        {
            await PickPhotoIntoAsync(
                bytes => CommunityPhotoBytes = bytes,
                "PickCommunityPhoto").ConfigureAwait(false);
        }

        private async Task PickGroupPhotoAsync()
        {
            await PickPhotoIntoAsync(
                bytes => GroupPhotoBytes = bytes,
                "PickGroupPhoto").ConfigureAwait(false);
        }

        private async Task PickPhotoIntoAsync(Action<byte[]> assign, string logTag)
        {
            if (_filePicker == null || assign == null)
            {
                return;
            }

            try
            {
                PickedChatMedia picked = await _filePicker.PickChatImageAsync().ConfigureAwait(false);
                if (picked == null || picked.Bytes == null || picked.Bytes.Length == 0)
                {
                    return;
                }

                if (_dispatcher != null)
                {
                    await _dispatcher.RunAsync(() => assign(picked.Bytes)).ConfigureAwait(false);
                }
                else
                {
                    assign(picked.Bytes);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NewConversation] " + logTag + ": " + ex.GetBaseException().Message);
            }
        }

        private async Task EditGroupEphemeralAsync()
        {
            if (_dialogs == null)
            {
                return;
            }

            GroupEphemeralDuration? chosen = await _dialogs
                .ShowGroupEphemeralAsync(_groupEphemeralDuration)
                .ConfigureAwait(false);
            if (!chosen.HasValue)
            {
                return;
            }

            if (_dispatcher != null)
            {
                await _dispatcher.RunAsync(() => GroupEphemeralDuration = chosen.Value)
                    .ConfigureAwait(false);
            }
            else
            {
                GroupEphemeralDuration = chosen.Value;
            }
        }

        private string FormatEphemeralLabel(GroupEphemeralDuration duration)
        {
            switch (duration)
            {
                case GroupEphemeralDuration.Hours24:
                    return _strings?.Get("CreateGroup_EphemeralStatus_24Hours", "24 hours") ?? "24 hours";
                case GroupEphemeralDuration.Days7:
                    return _strings?.Get("CreateGroup_EphemeralStatus_7Days", "7 days") ?? "7 days";
                case GroupEphemeralDuration.Days90:
                    return _strings?.Get("CreateGroup_EphemeralStatus_90Days", "90 days") ?? "90 days";
                default:
                    return _strings?.Get("CreateGroup_EphemeralStatus_Off", "Off") ?? "Off";
            }
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
            GroupPhotoBytes = null;
            GroupEphemeralDuration = GroupEphemeralDuration.Off;
            IsGroupPermissionsOpen = false;
            CommunityName = string.Empty;
            CommunityDescription = string.Empty;
            CommunityPhotoBytes = null;
            HasStartedCommunityCreate = false;
            SelectedMembers.Clear();
        }

        private void HeaderBack()
        {
            if (_isGroupPermissionsOpen)
            {
                IsGroupPermissionsOpen = false;
                return;
            }

            if (_createFlow != NewConversationCreateFlow.None)
            {
                CreateBack();
                return;
            }

            Close();
        }

        private void CreateBack()
        {
            if (_isGroupPermissionsOpen)
            {
                IsGroupPermissionsOpen = false;
                return;
            }

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
