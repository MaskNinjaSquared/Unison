// =============================================================================
// NewConversationViewModel.CreateFlow
//
// New Group / New Community / New Channel wizards hosted inside the New Conversation
// overlay. Confirm creates through IGroupService for group/community; channel is
// layout-only until the create API is wired.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Groups;
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
        private string _channelName = string.Empty;
        private string _channelDescription = string.Empty;
        private byte[] _channelPhotoBytes;
        private bool _hasStartedChannelCreate;
        private bool _isCreating;
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
        private const string GlyphChannel = "\uE8BD";
        private const int NewRowSettleMilliseconds = 100;

        public ObservableCollection<ChatItem> SelectedMembers { get; } =
            new ObservableCollection<ChatItem>();

        /// <summary>How many contacts are checked on the members pivot.</summary>
        public int SelectedMembersCount => SelectedMembers.Count;

        public ICommand CreateNextCommand { get; private set; }

        public ICommand CreateBackCommand { get; private set; }

        public ICommand StartCommunityCreateCommand { get; private set; }

        public ICommand StartChannelCreateCommand { get; private set; }

        public ICommand HeaderBackCommand { get; private set; }

        /// <summary>Opens the image picker and stores optimized JPEG/PNG bytes for the community photo.</summary>
        public ICommand PickCommunityPhotoCommand { get; private set; }

        /// <summary>Opens the image picker for the create-channel avatar.</summary>
        public ICommand PickChannelPhotoCommand { get; private set; }

        /// <summary>Opens the image picker for the create-group avatar.</summary>
        public ICommand PickGroupPhotoCommand { get; private set; }

        /// <summary>Opens the disappearing-messages dialog for create-group.</summary>
        public ICommand EditGroupEphemeralCommand { get; private set; }

        /// <summary>Shows the (stub) group-permissions overlay.</summary>
        public ICommand OpenGroupPermissionsCommand { get; private set; }

        /// <summary>Closes the group-permissions overlay.</summary>
        public ICommand CloseGroupPermissionsCommand { get; private set; }

        /// <summary>Info step → members pivot (first PivotItem).</summary>
        public ICommand ManageGroupMembersCommand { get; private set; }

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
                OnPropertyChanged(nameof(IsCreateChannel));
                OnPropertyChanged(nameof(ShowCommunityIntro));
                OnPropertyChanged(nameof(ShowChannelIntro));
                OnPropertyChanged(nameof(ShowCreateForm));
                OnPropertyChanged(nameof(ShowCreateExplainPane));
                OnPropertyChanged(nameof(ShowCreateActionChrome));
                RefreshCreateChrome();
            }
        }

        public bool IsCreateFlowOpen => _createFlow != NewConversationCreateFlow.None;

        public bool IsCreateGroup => _createFlow == NewConversationCreateFlow.Group;

        public bool IsCreateCommunity => _createFlow == NewConversationCreateFlow.Community;

        public bool IsCreateChannel => _createFlow == NewConversationCreateFlow.Channel;

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

        public string ChannelName
        {
            get => _channelName;
            set => Set(ref _channelName, value ?? string.Empty);
        }

        public string ChannelDescription
        {
            get => _channelDescription;
            set => Set(ref _channelDescription, value ?? string.Empty);
        }

        /// <summary>Picked channel avatar bytes (null when none). Cleared when leaving the flow.</summary>
        public byte[] ChannelPhotoBytes
        {
            get => _channelPhotoBytes;
            set
            {
                if (ReferenceEquals(_channelPhotoBytes, value))
                {
                    return;
                }

                _channelPhotoBytes = value;
                OnPropertyChanged(nameof(ChannelPhotoBytes));
                OnPropertyChanged(nameof(HasChannelPhoto));
            }
        }

        public bool HasChannelPhoto =>
            _channelPhotoBytes != null && _channelPhotoBytes.Length > 0;

        public bool HasStartedChannelCreate
        {
            get => _hasStartedChannelCreate;
            private set
            {
                if (!Set(ref _hasStartedChannelCreate, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(ShowChannelIntro));
                OnPropertyChanged(nameof(ShowCreateForm));
                OnPropertyChanged(nameof(ShowCreateExplainPane));
                OnPropertyChanged(nameof(ShowCreateActionChrome));
            }
        }

        /// <summary>True while group/community create is in flight (dark overlay + spinner; primary button disabled).</summary>
        public bool IsCreating
        {
            get => _isCreating;
            private set
            {
                if (!Set(ref _isCreating, value))
                {
                    return;
                }

                (CreateNextCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (CreateBackCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (HeaderBackCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>Minimal + community + not started → intro cover (persists across layout flips once started).</summary>
        public bool ShowCommunityIntro =>
            IsCreateCommunity && _isLayoutMinimal && !_hasStartedCommunityCreate;

        /// <summary>Minimal + channel + not started → intro cover (same rules as community).</summary>
        public bool ShowChannelIntro =>
            IsCreateChannel && _isLayoutMinimal && !_hasStartedChannelCreate;

        /// <summary>Form column (group always; community/channel after Começar or when not Minimal).</summary>
        public bool ShowCreateForm =>
            IsCreateGroup
            || (IsCreateCommunity && !ShowCommunityIntro)
            || (IsCreateChannel && !ShowChannelIntro);

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
            OnPropertyChanged(nameof(ShowChannelIntro));
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

            // A create in flight must not be cancelled by back; the overlay owns the screen.
            if (_isCreating)
            {
                return true;
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
            CreateNextCommand = new RelayCommand(() => _ = CreateNextAsync(), () => !_isCreating);
            CreateBackCommand = new RelayCommand(CreateBack, () => !_isCreating);
            StartCommunityCreateCommand = new RelayCommand(() => HasStartedCommunityCreate = true);
            StartChannelCreateCommand = new RelayCommand(() => HasStartedChannelCreate = true);
            HeaderBackCommand = new RelayCommand(HeaderBack, () => !_isCreating);
            PickCommunityPhotoCommand = new RelayCommand(
                () => _ = PickCommunityPhotoAsync(),
                () => _filePicker != null);
            PickChannelPhotoCommand = new RelayCommand(
                () => _ = PickChannelPhotoAsync(),
                () => _filePicker != null);
            PickGroupPhotoCommand = new RelayCommand(
                () => _ = PickGroupPhotoAsync(),
                () => _filePicker != null);
            EditGroupEphemeralCommand = new RelayCommand(() => _ = EditGroupEphemeralAsync());
            OpenGroupPermissionsCommand = new RelayCommand(() => IsGroupPermissionsOpen = true);
            CloseGroupPermissionsCommand = new RelayCommand(() => IsGroupPermissionsOpen = false);
            ManageGroupMembersCommand = new RelayCommand(
                () => GroupStepIndex = 0,
                () => IsCreateGroup);
            SelectedMembers.CollectionChanged -= SelectedMembers_CollectionChanged;
            SelectedMembers.CollectionChanged += SelectedMembers_CollectionChanged;
            RefreshCreateChrome();
        }

        private void SelectedMembers_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(SelectedMembersCount));
        }

        private async Task PickCommunityPhotoAsync()
        {
            await PickPhotoIntoAsync(
                bytes => CommunityPhotoBytes = bytes,
                "PickCommunityPhoto").ConfigureAwait(false);
        }

        private async Task PickChannelPhotoAsync()
        {
            await PickPhotoIntoAsync(
                bytes => ChannelPhotoBytes = bytes,
                "PickChannelPhoto").ConfigureAwait(false);
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

        private void OpenCreateChannel()
        {
            ResetCreateFields();
            ChannelDescription = string.Empty;
            CreateFlow = NewConversationCreateFlow.Channel;
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
            ChannelName = string.Empty;
            ChannelDescription = string.Empty;
            ChannelPhotoBytes = null;
            HasStartedChannelCreate = false;
            SelectedMembers.Clear();
        }

        private void HeaderBack()
        {
            if (_isCreating)
            {
                return;
            }

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
            if (_isCreating)
            {
                return;
            }

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

            // Channel create is layout-only until the newsletter API is wired.
            if (_createFlow == NewConversationCreateFlow.Channel)
            {
                return;
            }

            if (_groups == null)
            {
                await ShowNotImplementedAsync().ConfigureAwait(false);
                return;
            }

            if (_isCreating)
            {
                Debug.WriteLine("[NewConversation] Create ignored: one already in flight");
                return;
            }

            // Closing the overlay resets the flow, so later messages must not read it back.
            bool isCommunity = _createFlow == NewConversationCreateFlow.Community;
            IsCreating = true;
            try
            {
                if (isCommunity)
                {
                    await ConfirmCreateCommunityAsync().ConfigureAwait(false);
                }
                else if (_createFlow == NewConversationCreateFlow.Group)
                {
                    await ConfirmCreateGroupAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NewConversation] Create failed: " + ex.GetBaseException().Message);
                await ShowCreateFailureAsync(isCommunity, null).ConfigureAwait(false);
            }
            finally
            {
                // Create answers on the thread pool; the overlay binding must clear on the UI.
                await RunOnUiAsync(() => IsCreating = false).ConfigureAwait(false);
            }
        }

        private async Task ConfirmCreateGroupAsync()
        {
            var participants = new List<string>(SelectedMembers.Count);
            foreach (ChatItem member in SelectedMembers)
            {
                if (member != null && !string.IsNullOrWhiteSpace(member.JID))
                {
                    participants.Add(member.JID);
                }
            }

            var request = new GroupCreateRequest
            {
                Subject = _groupName,
                ParticipantJids = participants,
                PhotoBytes = _groupPhotoBytes,
                Ephemeral = _groupEphemeralDuration
            };

            GroupCreateResult result = await _groups.CreateGroupAsync(request).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await ShowCreateFailureAsync(false, result.FailureReason).ConfigureAwait(false);
                return;
            }

            string createdName = _groupName;
            _chats?.StartNewChat(result.GroupJid, createdName);
            // StartNewChat queues the row insert on the UI; opening before it lands finds no row.
            await Task.Delay(NewRowSettleMilliseconds).ConfigureAwait(false);
            await RunOnUiAsync(() =>
            {
                IsOpen = false;
                ChatOpenRequested?.Invoke(this, result.GroupJid);
            }).ConfigureAwait(false);

            // Close clears create-flow fields; keep the subject for the toast.
            NotifyCreatedSuccessfully(isCommunity: false, createdName);
            await ShowPartialFailureIfAnyAsync(false, result).ConfigureAwait(false);
        }

        private async Task ConfirmCreateCommunityAsync()
        {
            string createdName = _communityName;
            var request = new CommunityCreateRequest
            {
                Subject = createdName,
                Description = _communityDescription,
                PhotoBytes = _communityPhotoBytes
            };

            GroupCreateResult result = await _groups.CreateCommunityAsync(request).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await ShowCreateFailureAsync(true, result.FailureReason).ConfigureAwait(false);
                return;
            }

            _chats?.StartNewCommunity(result.GroupJid, createdName);
            await RunOnUiAsync(Close).ConfigureAwait(false);
            // The listing brings the linked announce/general subgroups under the seeded hub.
            _ = _groups.QueryAllGroupsAsync(force: true);
            NotifyCreatedSuccessfully(isCommunity: true, createdName);
            await ShowPartialFailureIfAnyAsync(true, result).ConfigureAwait(false);
        }

        private void NotifyCreatedSuccessfully(bool isCommunity, string name)
        {
            string title = isCommunity
                ? (_strings?.Get("CreateFlow_CommunityCreatedTitle", "Community created") ?? "Community created")
                : (_strings?.Get("CreateFlow_GroupCreatedTitle", "Group created") ?? "Group created");
            string bodyFormat = isCommunity
                ? (_strings?.Get("CreateFlow_CommunityCreatedBody", "{0} created successfully.")
                    ?? "{0} created successfully.")
                : (_strings?.Get("CreateFlow_GroupCreatedBody", "{0} created successfully.")
                    ?? "{0} created successfully.");
            string body = string.Format(bodyFormat, name ?? string.Empty);
            _notifications?.ShowToast(title, body);
        }

        private Task ShowCreateFailureAsync(bool isCommunity, string reason)
        {
            Debug.WriteLine("[NewConversation] Create " + (isCommunity ? "community" : "group")
                + " refused: " + (reason ?? "(exception)"));
            GroupCreateFailureText.Resolve(reason, isCommunity, out string key, out string fallback);
            return ShowCreateMessageAsync(isCommunity, _strings?.Get(key, fallback) ?? fallback);
        }

        private Task ShowPartialFailureIfAnyAsync(bool isCommunity, GroupCreateResult result)
        {
            if (!result.HasPartialFailure)
            {
                return Task.CompletedTask;
            }

            const string fallback =
                "Created, but the photo or disappearing messages could not be applied. You can change them in the info screen.";
            return ShowCreateMessageAsync(
                isCommunity,
                _strings?.Get("CreateFlow_PartialFailure", fallback) ?? fallback);
        }

        private async Task ShowCreateMessageAsync(bool isCommunity, string body)
        {
            string titleKey = isCommunity
                ? "NewConversation_NewCommunity.Text"
                : "NewConversation_NewGroup.Text";
            string titleFallback = isCommunity ? "New community" : "New group";
            string title = _strings?.Get(titleKey, titleFallback) ?? titleFallback;
            string close = _strings?.Get("Common_Close", "Close") ?? "Close";
            try
            {
                // Create answers on the thread pool; a ContentDialog built there throws and the
                // dialog service swallows it, so the user would see nothing at all.
                await RunOnUiAsync(() => _dialogs.ShowMessageAsync(title, body, close)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[NewConversation] Create message failed: " + ex.Message);
            }
        }

        private Task RunOnUiAsync(Action action)
        {
            if (_dispatcher != null)
            {
                return _dispatcher.RunAsync(action);
            }

            action();
            return Task.CompletedTask;
        }

        private async Task RunOnUiAsync(Func<Task> work)
        {
            if (_dispatcher == null)
            {
                await work().ConfigureAwait(false);
                return;
            }

            Task started = null;
            await _dispatcher.RunAsync(() => started = work()).ConfigureAwait(false);
            if (started != null)
            {
                await started.ConfigureAwait(false);
            }
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

            if (_createFlow == NewConversationCreateFlow.Channel)
            {
                HeaderTitle = _strings?.Get("NewConversation_NewChannel.Text", "New channel")
                    ?? "New channel";
                PrimaryActionLabel = _strings?.Get("CreateFlow_Confirm", "Confirm") ?? "Confirm";
                PrimaryActionGlyph = GlyphConfirm;
                CreateExplainGlyph = GlyphChannel;
                CreateExplainTitle = _strings?.Get(
                    "CreateChannel_ExplainTitle",
                    "Create a channel to reach an unlimited number of people.")
                    ?? "Create a channel to reach an unlimited number of people.";
                CreateExplainBody = string.Empty;
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
