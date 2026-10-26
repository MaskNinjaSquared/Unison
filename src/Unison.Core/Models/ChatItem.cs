using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unison.Core.Contracts;
using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    public class ChatItem : INotifyPropertyChanged, IAvatarSubject
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _id;
        public string Id
        {
            get => _id;
            set
            {
                if (_id == value) return;
                _id = value;
                OnPropertyChanged();
            }
        }

        private string _jid;
        public string JID
        {
            get => _jid;
            set
            {
                if (_jid == value) return;
                _jid = value;
                OnPropertyChanged();
            }
        }

        private string _name;
        public string Name
        {
            get => _name;
            set
            {
                // Titles / favourites / SemanticZoom never keep a leading "~" (strip-only mark).
                string next = DisplayNameResolution.StripContactIndicator(value);
                if (_name == next) return;
                _name = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Initial));
            }
        }

        private readonly ChatListPreview _lastPreview = new ChatListPreview();

        /// <summary>Last-message strip DTO (kind, body, author, outgoing ticks).</summary>
        public ChatListPreview LastPreview => _lastPreview;

        /// <summary>Notifies list bindings after mutating nested <see cref="LastPreview"/> fields.</summary>
        public void RaiseLastPreviewChanged()
        {
            OnPropertyChanged(nameof(LastPreview));
            OnPropertyChanged(nameof(LastMessage));
            OnPropertyChanged(nameof(LastMessageAuthor));
            OnPropertyChanged(nameof(HasLastMessageAuthor));
        }

        public string LastMessage
        {
            get => _lastPreview.Text;
            set
            {
                if (_lastPreview.Text == value) return;
                _lastPreview.Text = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastPreview));
            }
        }

        /// <summary>Group strip prefix ("Alice: "), empty for DMs.</summary>
        public string LastMessageAuthor
        {
            get => _lastPreview.Author;
            set
            {
                if (_lastPreview.Author == value) return;
                _lastPreview.Author = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLastMessageAuthor));
                OnPropertyChanged(nameof(LastPreview));
            }
        }

        public bool HasLastMessageAuthor => !string.IsNullOrEmpty(_lastPreview.Author);

        /// <summary>
        /// Who wrote <see cref="LastMessage"/> in a group, kept so the strip can be recomposed when
        /// the name behind a LID/phone arrives later (history sync names the sender long after the
        /// message). Not the display text — that is <see cref="LastMessageAuthor"/>.
        /// </summary>
        public string LastMessageParticipantJid { get; set; }

        /// <summary>Best name known for <see cref="LastMessageParticipantJid"/> when the strip was built.</summary>
        public string LastMessageSenderName { get; set; }

        /// <summary>Newest message was ours, so the strip uses the localized "You" label.</summary>
        public bool LastMessageIsFromMe
        {
            get => _lastPreview.IsFromMe;
            set
            {
                if (_lastPreview.IsFromMe == value) return;
                _lastPreview.IsFromMe = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastPreview));
            }
        }

        /// <summary>Outgoing ticks for the list strip; <see cref="MessageSendState.NotApplicable"/> when incoming.</summary>
        public MessageSendState LastMessageSendState
        {
            get => _lastPreview.SendState;
            set
            {
                if (_lastPreview.SendState == value) return;
                _lastPreview.SendState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastPreview));
            }
        }

        /// <summary>JIDs mentioned in <see cref="LastMessage"/> (for @alias resolution in the list strip).</summary>
        public System.Collections.Generic.List<string> LastMessageMentionedJids
        {
            get => _lastPreview.MentionedJids;
            set
            {
                if (ReferenceEquals(_lastPreview.MentionedJids, value)) return;
                _lastPreview.MentionedJids = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastPreview));
            }
        }

        /// <summary>Category for the chat-list preview (text / image / video / sticker / voice).</summary>
        public ChatPreviewKind LastMessageKind
        {
            get => _lastPreview.Kind;
            set
            {
                if (_lastPreview.Kind == value) return;
                _lastPreview.Kind = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastPreview));
                RaiseLastMessageKindFlagsChanged();
            }
        }

        public bool IsLastMessageImage => _lastPreview.IsImage;
        public bool IsLastMessageVideo => _lastPreview.IsVideo;
        public bool IsLastMessageSticker => _lastPreview.IsSticker;
        public bool IsLastMessageVoice => _lastPreview.IsVoice;
        public bool IsLastMessageText => _lastPreview.IsText;

        private string _timestamp;
        public string Timestamp
        {
            get => _timestamp;
            set
            {
                if (_timestamp == value) return;
                _timestamp = value;
                OnPropertyChanged();
            }
        }

        private DateTime? _lastMessageTimestampUtc;
        public DateTime? LastMessageTimestampUtc
        {
            get => _lastMessageTimestampUtc;
            set
            {
                if (_lastMessageTimestampUtc == value) return;
                _lastMessageTimestampUtc = value;
                OnPropertyChanged();
            }
        }

        private string _lastMessageId;
        /// <summary>MessageId of the tip currently shown on the list strip.</summary>
        public string LastMessageId
        {
            get => _lastMessageId;
            set
            {
                if (string.Equals(_lastMessageId, value, StringComparison.Ordinal)) return;
                _lastMessageId = value;
                OnPropertyChanged();
            }
        }

        private int _unreadCount;
        public int UnreadCount
        {
            get => _unreadCount;
            set
            {
                if (_unreadCount == value) return;
                _unreadCount = value;
                OnPropertyChanged();
                RaiseUnreadUiChanged();
            }
        }

        public string UnreadText => _unreadCount > 999 ? "999+" : _unreadCount.ToString();

        /// <summary>True when there are unread messages (bind with BooleanToVisibilityConverter).</summary>
        public bool HasUnread => _unreadCount > 0;

        private bool _isFavorite;

        /// <summary>
        /// WhatsApp Favourites list (app-state <c>favoritesAction</c>).
        /// Separate from chat-list pin (<see cref="IsChatPinned"/>).
        /// </summary>
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite == value) return;
                _isFavorite = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Local unsent draft. Always false until drafts are stored on the chat row.
        /// </summary>
        public bool HasDraft => false;

        private string _avatarUrl;
        /// <summary>Preview / low-res file. Display through <see cref="GetAvatarUrl"/>.</summary>
        public string AvatarUrl
        {
            get => _avatarUrl;
            set
            {
                if (_avatarUrl == value) return;
                _avatarUrl = value;
                OnPropertyChanged();
            }
        }

        private string _avatarHighUrl;
        /// <summary>Full-size picture (Baileys type=image), cached as *_high.jpg. Display through <see cref="GetAvatarUrl"/>.</summary>
        public string AvatarHighUrl
        {
            get => _avatarHighUrl;
            set
            {
                if (_avatarHighUrl == value) return;
                _avatarHighUrl = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The URI the UI should show. Each size falls back to the other so a chat with only
        /// preview still has a photo in the info pane, and a chat with only the high file still
        /// has one in the list.
        /// </summary>
        /// <param name="preferHigh">True for large surfaces (info, header); false for the list.</param>
        public string GetAvatarUrl(bool preferHigh)
        {
            return GetAvatarUrl(_avatarUrl, _avatarHighUrl, preferHigh);
        }

        /// <summary>
        /// Overload for compiled list bindings: x:Bind cannot take a bool literal, so the list
        /// path is preview-first and re-evaluates when either URI property changes.
        /// </summary>
        public string GetAvatarUrl(string previewUrl, string highUrl)
        {
            return GetAvatarUrl(previewUrl, highUrl, false);
        }

        public string GetAvatarUrl(string previewUrl, string highUrl, bool preferHigh)
        {
            return preferHigh
                ? (!string.IsNullOrWhiteSpace(highUrl) ? highUrl : previewUrl)
                : (!string.IsNullOrWhiteSpace(previewUrl) ? previewUrl : highUrl);
        }

        private int _groupMemberCount;
        /// <summary>Participant count from group metadata. 0 = not yet known.</summary>
        public int GroupMemberCount
        {
            get => _groupMemberCount;
            set
            {
                if (_groupMemberCount == value) return;
                _groupMemberCount = value;
                OnPropertyChanged();
            }
        }

        private System.Collections.Generic.List<GroupMember> _groupMembers;
        private System.Collections.Generic.IReadOnlyDictionary<string, string> _mentionLookup =
            MentionLookupBuilder.Empty;

        /// <summary>
        /// Roster from the last group metadata/listing. Null/empty until a query returns participants.
        /// Chat-info Members binds this list; bubbles/strip use <see cref="MentionLookup"/>.
        /// </summary>
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public System.Collections.Generic.List<GroupMember> GroupMembers
        {
            get => _groupMembers;
            set
            {
                var next = (value != null && value.Count > 0) ? value : null;
                if (ReferenceEquals(_groupMembers, next)) return;
                _groupMembers = next;
                if (next == null)
                {
                    _hasFullGroupRoster = false;
                }

                _mentionLookup = MentionLookupBuilder.FromRoster(_groupMembers);
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasGroupMembers));
                OnPropertyChanged(nameof(HasFullGroupRoster));
                OnPropertyChanged(nameof(MentionLookup));
            }
        }

        [Newtonsoft.Json.JsonIgnore]
        public bool HasGroupMembers => _groupMembers != null && _groupMembers.Count > 0;

        private bool _hasFullGroupRoster;
        /// <summary>
        /// True after store/IQ applied the complete participant list. False while empty or only
        /// <see cref="GroupMember.IsMinimal"/> stubs from the open timeline (Mobile).
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool HasFullGroupRoster
        {
            get => _hasFullGroupRoster;
            set
            {
                if (_hasFullGroupRoster == value) return;
                _hasFullGroupRoster = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Digit → name map from <see cref="GroupMembers"/>, rebuilt when the roster is replaced.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public System.Collections.Generic.IReadOnlyDictionary<string, string> MentionLookup =>
            _mentionLookup ?? MentionLookupBuilder.Empty;

        /// <summary>
        /// Rebuilds <see cref="MentionLookup"/> after in-place roster field merges (no list replace).
        /// </summary>
        public void RefreshMentionLookupFromRoster()
        {
            _mentionLookup = MentionLookupBuilder.FromRoster(_groupMembers);
            OnPropertyChanged(nameof(MentionLookup));
        }

        private string _avatarPictureId;
        /// <summary>
        /// Server picture id from the last successful IQ. Used to skip CDN download when
        /// a later open-chat refresh returns the same id.
        /// </summary>
        public string AvatarPictureId
        {
            get => _avatarPictureId;
            set
            {
                if (_avatarPictureId == value) return;
                _avatarPictureId = value;
                OnPropertyChanged();
            }
        }

        private string _avatarRemoteUrl;
        /// <summary>
        /// Last remote preview URL when <see cref="AvatarPictureId"/> was missing. Fallback
        /// equality key before downloading again.
        /// </summary>
        public string AvatarRemoteUrl
        {
            get => _avatarRemoteUrl;
            set
            {
                if (_avatarRemoteUrl == value) return;
                _avatarRemoteUrl = value;
                OnPropertyChanged();
            }
        }

        private DateTime? _avatarFetchedAtUtc;
        public DateTime? AvatarFetchedAtUtc
        {
            get => _avatarFetchedAtUtc;
            set
            {
                if (_avatarFetchedAtUtc == value) return;
                _avatarFetchedAtUtc = value;
                OnPropertyChanged();
            }
        }

        private DateTime? _avatarFetchFailedAtUtc;
        public DateTime? AvatarFetchFailedAtUtc
        {
            get => _avatarFetchFailedAtUtc;
            set
            {
                if (_avatarFetchFailedAtUtc == value) return;
                _avatarFetchFailedAtUtc = value;
                OnPropertyChanged();
            }
        }

        private string _avatarFetchFailureReason;
        public string AvatarFetchFailureReason
        {
            get => _avatarFetchFailureReason;
            set
            {
                if (_avatarFetchFailureReason == value) return;
                _avatarFetchFailureReason = value;
                OnPropertyChanged();
            }
        }

        private ChatKind _kind = ChatKind.Direct;

        /// <summary>Direct (1:1), Group, Personal, or Community (reserved).</summary>
        public ChatKind Kind
        {
            get => _kind;
            set
            {
                if (_kind == value) return;
                _kind = value;
                OnPropertyChanged();
                RaiseChatKindFlagsChanged();
            }
        }

        /// <summary>True when <see cref="Kind"/> is <see cref="ChatKind.Group"/>.</summary>
        public bool IsGroup
        {
            get => _kind == ChatKind.Group;
            set
            {
                if (value)
                {
                    Kind = ChatKind.Group;
                }
                else if (_kind == ChatKind.Group)
                {
                    // Keep Personal if something only clears the group flag.
                    Kind = ChatKind.Direct;
                }
            }
        }

        /// <summary>True when this is a notes-to-self / “Message yourself” chat.</summary>
        public bool IsPersonal => _kind == ChatKind.Personal;

        /// <summary>True when this is a normal 1:1 with another person.</summary>
        public bool IsDirect => _kind == ChatKind.Direct;

        /// <summary>True when <see cref="Kind"/> is <see cref="ChatKind.Community"/> (reserved).</summary>
        public bool IsCommunity => _kind == ChatKind.Community;

        /// <summary>
        /// UI label for list/header. Uses <see cref="IStringResources"/> (DI) for Personal markers
        /// and community-announce “Avisos”. Does not mutate <see cref="Name"/>.
        /// </summary>
        public string GetNameResolved(IStringResources strings)
        {
            if (IsCommunityAnnounce)
            {
                return strings != null
                    ? strings.Get("Chat_CommunityAnnouncements", "Announcements")
                    : "Announcements";
            }

            switch (Kind)
            {
                case ChatKind.Personal:
                    {
                        string marker = strings != null
                            ? strings.Get("Chat_SelfMarker", "(You)")
                            : "(You)";
                        string fallback = strings != null
                            ? strings.Get("Chat_SelfFallbackName", "You")
                            : "You";
                        return SelfChatNaming.FormatDisplayName(Name, true, marker, fallback);
                    }

                case ChatKind.Group:
                    return Name ?? string.Empty;

                case ChatKind.Community:
                    if (!string.IsNullOrWhiteSpace(Name))
                    {
                        return Name;
                    }

                    return strings != null
                        ? strings.Get("Chat_CommunityFallbackName", "Community")
                        : "Community";

                case ChatKind.Direct:
                default:
                    // Never surface a leading "~" in titles / favourites / SemanticZoom keys.
                    return DisplayNameResolution.StripContactIndicator(Name) ?? string.Empty;
            }
        }

        /// <summary>
        /// Sets <see cref="Kind"/> from JID + self flag (group wins over self).
        /// Community parents keep <see cref="ChatKind.Community"/> via <see cref="IsCommunityParent"/>.
        /// Strips a persisted “(You)” suffix from <see cref="Name"/> when Personal.
        /// </summary>
        public void ApplyKind(string jid, bool isSelfChat)
        {
            if (_isCommunityParent)
            {
                Kind = ChatKind.Community;
                return;
            }

            Kind = JidHelper.ResolveKind(jid ?? JID, isSelfChat);
            if (Kind == ChatKind.Personal)
            {
                string clean = SelfChatNaming.StripMarker(Name);
                if (clean != null || SelfChatNaming.IsSelfMarkerLabel(Name))
                {
                    Name = clean;
                }
            }
        }

        private ChatStatus _status = ChatStatus.Active;

        /// <summary>SQLite-backed conversation lifecycle.</summary>
        public ChatStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                bool wasArchived = _status == ChatStatus.Archived;
                _status = value;
                OnPropertyChanged();
                if (wasArchived != (_status == ChatStatus.Archived))
                {
                    OnPropertyChanged(nameof(IsArchived));
                }
            }
        }

        /// <summary>
        /// Compatibility projection for app-state/archive payloads and legacy snapshots.
        /// </summary>
        public bool IsArchived
        {
            get => Status == ChatStatus.Archived;
            set
            {
                if (value)
                {
                    Status = ChatStatus.Archived;
                }
                else if (Status == ChatStatus.Archived)
                {
                    Status = ChatStatus.Active;
                }
            }
        }

        private bool _isChatPinned;
        /// <summary>WhatsApp chat-list pin (from history / app-state). JSON key kept as IsPinned.</summary>
        [Newtonsoft.Json.JsonProperty("IsPinned")]
        public bool IsChatPinned
        {
            get => _isChatPinned;
            set
            {
                if (_isChatPinned == value) return;
                _isChatPinned = value;
                OnPropertyChanged();
            }
        }

        private bool _isWidgetPinned;
        /// <summary>Start live-tile for the conversation (<c>chat=</c> launch).</summary>
        public bool IsWidgetPinned
        {
            get => _isWidgetPinned;
            set
            {
                if (_isWidgetPinned == value) return;
                _isWidgetPinned = value;
                OnPropertyChanged();
            }
        }

        private bool _isContactWidgetPinned;
        /// <summary>
        /// Start live-tile for the contact Profile (<c>profile=</c> launch).
        /// Synced from the SecondaryTile presence (not SQLite).
        /// </summary>
        public bool IsContactWidgetPinned
        {
            get => _isContactWidgetPinned;
            set
            {
                if (_isContactWidgetPinned == value) return;
                _isContactWidgetPinned = value;
                OnPropertyChanged();
            }
        }

        private long? _pinnedTimestamp;
        public long? PinnedTimestamp
        {
            get => _pinnedTimestamp;
            set
            {
                if (_pinnedTimestamp == value) return;
                _pinnedTimestamp = value;
                OnPropertyChanged();
            }
        }

        private long? _mutedUntil;
        /// <summary>
        /// Unix seconds mute deadline (local + WhatsApp). Null = not muted; forever ≈ year 2999.
        /// JSON key kept as MuteEndTimestamp.
        /// </summary>
        [Newtonsoft.Json.JsonProperty("MuteEndTimestamp")]
        public long? MutedUntil
        {
            get => _mutedUntil;
            set
            {
                if (_mutedUntil == value) return;
                _mutedUntil = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMutedLocally));
            }
        }

        /// <summary>Business logic over <see cref="MutedUntil"/>.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool IsMutedLocally => Helpers.ChatMuteHelper.IsMuted(_mutedUntil);

        private bool _isAnnounceOnly;
        /// <summary>
        /// Group "announcement" mode: only admins may send messages (child node
        /// <c>announcement</c> in w:g2 group metadata). Ignored for non-groups.
        /// </summary>
        public bool IsAnnounceOnly
        {
            get => _isAnnounceOnly;
            set
            {
                if (_isAnnounceOnly == value) return;
                _isAnnounceOnly = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGroupLockedForMessages));
            }
        }

        private bool _isCommunityAnnounce;
        /// <summary>
        /// Community default announce channel (<c>default_sub_group</c> in w:g2).
        /// Stays <see cref="ChatKind.Group"/>; UI shows localized “Announcements” / “Avisos”.
        /// </summary>
        public bool IsCommunityAnnounce
        {
            get => _isCommunityAnnounce;
            set
            {
                if (_isCommunityAnnounce == value) return;
                _isCommunityAnnounce = value;
                OnPropertyChanged();
            }
        }

        private bool _isCommunityParent;
        /// <summary>
        /// Community parent hub (<c>parent</c> in w:g2). Sets <see cref="Kind"/> to
        /// <see cref="ChatKind.Community"/> while true.
        /// </summary>
        public bool IsCommunityParent
        {
            get => _isCommunityParent;
            set
            {
                if (_isCommunityParent == value) return;
                _isCommunityParent = value;
                OnPropertyChanged();
                if (value)
                {
                    Kind = ChatKind.Community;
                }
                else if (_kind == ChatKind.Community)
                {
                    Kind = ChatKind.Group;
                }
            }
        }

        private string _linkedParentJid;
        /// <summary>
        /// Parent community JID when this chat is a linked subgroup (<c>linked_parent</c>).
        /// </summary>
        public string LinkedParentJid
        {
            get => _linkedParentJid;
            set
            {
                if (string.Equals(_linkedParentJid, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _linkedParentJid = value;
                OnPropertyChanged();
            }
        }

        private string _linkedParentDisplayName;
        /// <summary>
        /// Runtime community title stamped from the hub row for chat-detail headers.
        /// Not persisted — synthetic parents often never appear in the chat catalog.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public string LinkedParentDisplayName
        {
            get => _linkedParentDisplayName;
            set
            {
                string next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (string.Equals(_linkedParentDisplayName, next, StringComparison.Ordinal))
                {
                    return;
                }

                _linkedParentDisplayName = next;
                OnPropertyChanged();
            }
        }

        private readonly ObservableCollection<ChatItem> _communityMembers =
            new ObservableCollection<ChatItem>();

        /// <summary>
        /// Runtime-only linked groups under this community hub. Not persisted.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public ObservableCollection<ChatItem> CommunityMembers => _communityMembers;

        /// <summary>Number of linked groups currently attached to this hub.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public int CommunityGroupCount => _communityMembers.Count;

        /// <summary>Accessible count for bindings / subtitle formatting.</summary>
        public int GetCommunityGroupCount() => _communityMembers.Count;

        /// <summary>Accessible snapshot of linked groups.</summary>
        public IReadOnlyList<ChatItem> GetCommunityGroups()
        {
            if (_communityMembers.Count == 0)
            {
                return Array.Empty<ChatItem>();
            }

            var copy = new List<ChatItem>(_communityMembers.Count);
            for (int i = 0; i < _communityMembers.Count; i++)
            {
                copy.Add(_communityMembers[i]);
            }

            return copy;
        }

        /// <summary>
        /// Hub subtitle: “Community · N groups” (localized via <paramref name="strings"/>).
        /// </summary>
        public string GetCommunitySubtitle(IStringResources strings)
        {
            int count = GetCommunityGroupCount();
            string format = strings != null
                ? strings.Get("Chat_CommunityHubSubtitle", "Community · {0} groups")
                : "Community · {0} groups";
            try
            {
                return string.Format(format, count);
            }
            catch (FormatException)
            {
                return format + " " + count;
            }
        }

        /// <summary>Replaces the runtime member list and raises count notifications.</summary>
        public void SetCommunityGroups(IReadOnlyList<ChatItem> groups)
        {
            _communityMembers.Clear();
            if (groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    ChatItem group = groups[i];
                    if (group != null)
                    {
                        _communityMembers.Add(group);
                    }
                }
            }

            OnPropertyChanged(nameof(CommunityMembers));
            OnPropertyChanged(nameof(CommunityGroupCount));
        }

        /// <summary>Clears runtime members when this row is no longer a hub in the list.</summary>
        public void ClearCommunityGroups()
        {
            if (_communityMembers.Count == 0)
            {
                return;
            }

            _communityMembers.Clear();
            OnPropertyChanged(nameof(CommunityMembers));
            OnPropertyChanged(nameof(CommunityGroupCount));
        }

        private GroupParticipantRole _myGroupRole = GroupParticipantRole.Member;
        /// <summary>
        /// Logged-in user's role in this group (from metadata <c>participant admin=...</c>).
        /// </summary>
        public GroupParticipantRole MyGroupRole
        {
            get => _myGroupRole;
            set
            {
                if (_myGroupRole == value) return;
                _myGroupRole = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGroupAdmin));
                OnPropertyChanged(nameof(IsGroupLockedForMessages));
            }
        }

        /// <summary>True when <see cref="MyGroupRole"/> is Admin or SuperAdmin.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool IsGroupAdmin =>
            _myGroupRole == GroupParticipantRole.Admin ||
            _myGroupRole == GroupParticipantRole.SuperAdmin;

        /// <summary>
        /// True when this is a group in announce-only mode and the current user is not an admin —
        /// i.e. the composer should be locked for sending.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool IsGroupLockedForMessages =>
            IsGroup && _isAnnounceOnly && !IsGroupAdmin;

        public string Initial => !string.IsNullOrEmpty(Name) ? Name.Substring(0, 1).ToUpper() : "?";

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void Raise(params string[] propertyNames)
        {
            if (propertyNames == null)
            {
                return;
            }

            for (int i = 0; i < propertyNames.Length; i++)
            {
                OnPropertyChanged(propertyNames[i]);
            }
        }

        private void RaiseLastMessageKindFlagsChanged()
        {
            Raise(
                nameof(IsLastMessageImage),
                nameof(IsLastMessageVideo),
                nameof(IsLastMessageSticker),
                nameof(IsLastMessageVoice),
                nameof(IsLastMessageText));
        }

        private void RaiseUnreadUiChanged()
        {
            Raise(nameof(UnreadText), nameof(HasUnread));
        }

        private void RaiseChatKindFlagsChanged()
        {
            Raise(
                nameof(IsGroup),
                nameof(IsPersonal),
                nameof(IsDirect),
                nameof(IsCommunity),
                nameof(IsGroupLockedForMessages));
        }
    }
}
