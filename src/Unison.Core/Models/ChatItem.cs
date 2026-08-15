using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unison.Core.Contracts;
using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    public class ChatItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _id;
        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        private string _jid;
        public string JID
        {
            get => _jid;
            set { _jid = value; OnPropertyChanged(); }
        }

        private string _name;
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Initial)); }
        }

        private string _lastMessage;
        public string LastMessage
        {
            get => _lastMessage;
            set { _lastMessage = value; OnPropertyChanged(); }
        }

        private string _lastMessageAuthor;
        /// <summary>Group strip prefix ("Alice: "), empty for DMs.</summary>
        public string LastMessageAuthor
        {
            get => _lastMessageAuthor;
            set
            {
                if (_lastMessageAuthor == value) return;
                _lastMessageAuthor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLastMessageAuthor));
            }
        }

        public bool HasLastMessageAuthor => !string.IsNullOrEmpty(_lastMessageAuthor);

        private System.Collections.Generic.List<string> _lastMessageMentionedJids;
        /// <summary>JIDs mentioned in <see cref="LastMessage"/> (for @alias resolution in the list strip).</summary>
        public System.Collections.Generic.List<string> LastMessageMentionedJids
        {
            get => _lastMessageMentionedJids;
            set
            {
                _lastMessageMentionedJids = value;
                OnPropertyChanged();
            }
        }

        private ChatPreviewKind _lastMessageKind;
        /// <summary>Category for the chat-list preview (text / image / video / sticker / voice).</summary>
        public ChatPreviewKind LastMessageKind
        {
            get => _lastMessageKind;
            set
            {
                if (_lastMessageKind == value) return;
                _lastMessageKind = value;
                OnPropertyChanged();
                RaiseLastMessageKindFlagsChanged();
            }
        }

        public bool IsLastMessageImage => _lastMessageKind == ChatPreviewKind.Image;
        public bool IsLastMessageVideo => _lastMessageKind == ChatPreviewKind.Video;
        public bool IsLastMessageSticker => _lastMessageKind == ChatPreviewKind.Sticker;
        public bool IsLastMessageVoice => _lastMessageKind == ChatPreviewKind.Voice;
        public bool IsLastMessageText => _lastMessageKind == ChatPreviewKind.Text;

        private string _timestamp;
        public string Timestamp
        {
            get => _timestamp;
            set { _timestamp = value; OnPropertyChanged(); }
        }

        private DateTime? _lastMessageTimestampUtc;
        public DateTime? LastMessageTimestampUtc
        {
            get => _lastMessageTimestampUtc;
            set { _lastMessageTimestampUtc = value; OnPropertyChanged(); }
        }

        private int _unreadCount;
        public int UnreadCount
        {
            get => _unreadCount;
            set
            {
                _unreadCount = value;
                OnPropertyChanged();
                RaiseUnreadUiChanged();
            }
        }

        public string UnreadText => _unreadCount > 999 ? "999+" : _unreadCount.ToString();

        /// <summary>True when there are unread messages (bind with BooleanToVisibilityConverter).</summary>
        public bool HasUnread => _unreadCount > 0;

        private string _avatarUrl;
        /// <summary>Preview / low-res file. Display through <see cref="GetAvatarUrl"/>.</summary>
        public string AvatarUrl
        {
            get => _avatarUrl;
            set { _avatarUrl = value; OnPropertyChanged(); }
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

        private DateTime? _avatarFetchedAtUtc;
        public DateTime? AvatarFetchedAtUtc
        {
            get => _avatarFetchedAtUtc;
            set { _avatarFetchedAtUtc = value; OnPropertyChanged(); }
        }

        private DateTime? _avatarFetchFailedAtUtc;
        public DateTime? AvatarFetchFailedAtUtc
        {
            get => _avatarFetchFailedAtUtc;
            set { _avatarFetchFailedAtUtc = value; OnPropertyChanged(); }
        }

        private string _avatarFetchFailureReason;
        public string AvatarFetchFailureReason
        {
            get => _avatarFetchFailureReason;
            set { _avatarFetchFailureReason = value; OnPropertyChanged(); }
        }

        private ChatKind _kind = ChatKind.Direct;

        /// <summary>Direct (1:1), Group, or Personal (chat with yourself).</summary>
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

        /// <summary>
        /// UI label for list/header. Uses <see cref="IStringResources"/> (DI) for Personal markers.
        /// Does not mutate <see cref="Name"/>.
        /// </summary>
        public string GetNameResolved(IStringResources strings)
        {
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

                case ChatKind.Direct:
                default:
                    return Name ?? string.Empty;
            }
        }

        /// <summary>
        /// Sets <see cref="Kind"/> from JID + self flag (group wins over self).
        /// Strips a persisted “(You)” suffix from <see cref="Name"/> when Personal.
        /// </summary>
        public void ApplyKind(string jid, bool isSelfChat)
        {
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

        private bool _isArchived;
        public bool IsArchived
        {
            get => _isArchived;
            set { _isArchived = value; OnPropertyChanged(); }
        }

        private bool _isChatPinned;
        /// <summary>WhatsApp chat-list pin (from history / app-state). JSON key kept as IsPinned.</summary>
        [Newtonsoft.Json.JsonProperty("IsPinned")]
        public bool IsChatPinned
        {
            get => _isChatPinned;
            set { _isChatPinned = value; OnPropertyChanged(); }
        }

        private ChatLocalStatus _localStatus = ChatLocalStatus.Active;
        /// <summary>SQLite local lifecycle (Active / Deleted / Ignored). Mute uses <see cref="MutedUntil"/>.</summary>
        public ChatLocalStatus LocalStatus
        {
            get => _localStatus;
            set
            {
                if (_localStatus == value) return;
                _localStatus = value;
                OnPropertyChanged();
            }
        }

        private bool _isWidgetPinned;
        /// <summary>Start live-tile / secondary tile pin (SQLite).</summary>
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

        private long? _pinnedTimestamp;
        public long? PinnedTimestamp
        {
            get => _pinnedTimestamp;
            set { _pinnedTimestamp = value; OnPropertyChanged(); }
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
            Raise(nameof(IsGroup), nameof(IsPersonal), nameof(IsDirect), nameof(IsGroupLockedForMessages));
        }
    }
}
