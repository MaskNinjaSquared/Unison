using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    /// <summary>
    /// SQLite-backed local chat metadata (first step toward moving chats off JSON).
    /// </summary>
    /// <remarks>
    /// Property setters mark the field as known. A stub created only to remember a mute must not
    /// answer "not pinned" when <c>ApplyTo</c> reads it — that was wiping chat-list pins mid-sync.
    /// </remarks>
    public sealed class ChatLocalState
    {
        private string _jid;
        private ChatStatus _status = ChatStatus.Active;
        private bool _isChatPinned;
        private long? _pinnedTimestamp;
        private bool _isWidgetPinned;
        private bool _isFavorite;
        private long? _mutedUntil;
        private ChannelViewerRole _channelRole;
        private ChatLocalStateFields _known;

        public string Jid
        {
            get { return _jid; }
            set { _jid = value; }
        }

        public ChatStatus Status
        {
            get { return _status; }
            set
            {
                _status = value;
                _known |= ChatLocalStateFields.Status;
            }
        }

        /// <summary>WhatsApp chat-list pin mirrored during history/app-state sync.</summary>
        public bool IsChatPinned
        {
            get { return _isChatPinned; }
            set
            {
                _isChatPinned = value;
                _known |= ChatLocalStateFields.Pin;
            }
        }

        /// <summary>
        /// Unix-seconds sort key assigned when WhatsApp pinned this chat. It travels with the pin
        /// state so message activity cannot replace the user's pinned-chat order after a restart.
        /// </summary>
        public long? PinnedTimestamp
        {
            get { return _pinnedTimestamp; }
            set { _pinnedTimestamp = value; }
        }

        /// <summary>Start live-tile / secondary tile pin.</summary>
        public bool IsWidgetPinned
        {
            get { return _isWidgetPinned; }
            set
            {
                _isWidgetPinned = value;
                _known |= ChatLocalStateFields.WidgetPin;
            }
        }

        /// <summary>WhatsApp Favourites list membership (favoritesAction).</summary>
        public bool IsFavorite
        {
            get { return _isFavorite; }
            set
            {
                _isFavorite = value;
                _known |= ChatLocalStateFields.Favorite;
            }
        }

        /// <summary>Unix seconds mute deadline; null = not muted. Forever = year 2999.</summary>
        public long? MutedUntil
        {
            get { return _mutedUntil; }
            set
            {
                _mutedUntil = value;
                _known |= ChatLocalStateFields.Mute;
            }
        }

        /// <summary>This account's role in a channel, as the server last answered it.</summary>
        public ChannelViewerRole ChannelRole
        {
            get { return _channelRole; }
            set
            {
                _channelRole = value;
                _known |= ChatLocalStateFields.ChannelRole;
            }
        }

        public bool IsMutedLocally => ChatMuteHelper.IsMuted(MutedUntil);

        public ChatLocalStateFields KnownFields
        {
            get { return _known; }
            set { _known = value; }
        }

        public bool Knows(ChatLocalStateFields field)
        {
            return (_known & field) == field;
        }
    }
}
