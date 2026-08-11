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
                OnPropertyChanged(nameof(IsLastMessageImage));
                OnPropertyChanged(nameof(IsLastMessageVideo));
                OnPropertyChanged(nameof(IsLastMessageSticker));
                OnPropertyChanged(nameof(IsLastMessageVoice));
                OnPropertyChanged(nameof(IsLastMessageText));
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
                OnPropertyChanged(nameof(UnreadText));
                OnPropertyChanged(nameof(HasUnread));
            }
        }

        public string UnreadText => _unreadCount > 999 ? "999+" : _unreadCount.ToString();

        /// <summary>True when there are unread messages (bind with BooleanToVisibilityConverter).</summary>
        public bool HasUnread => _unreadCount > 0;

        private string _avatarUrl;
        public string AvatarUrl
        {
            get => _avatarUrl;
            set { _avatarUrl = value; OnPropertyChanged(); }
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
                OnPropertyChanged(nameof(IsGroup));
                OnPropertyChanged(nameof(IsPersonal));
                OnPropertyChanged(nameof(IsDirect));
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

        private bool _isPinned;
        public bool IsPinned
        {
            get => _isPinned;
            set { _isPinned = value; OnPropertyChanged(); }
        }

        private long? _pinnedTimestamp;
        public long? PinnedTimestamp
        {
            get => _pinnedTimestamp;
            set { _pinnedTimestamp = value; OnPropertyChanged(); }
        }

        private long? _muteEndTimestamp;
        public long? MuteEndTimestamp
        {
            get => _muteEndTimestamp;
            set { _muteEndTimestamp = value; OnPropertyChanged(); }
        }

        public string Initial => !string.IsNullOrEmpty(Name) ? Name.Substring(0, 1).ToUpper() : "?";

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
