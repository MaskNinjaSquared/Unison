using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    /// <summary>
    /// SQLite-backed local chat metadata (first step toward moving chats off JSON).
    /// </summary>
    /// <remarks>
    /// Property setters mark the field as known. A stub created only to remember a mute must not
    /// answer "not pinned" when <c>ApplyTo</c> reads it — that was wiping favourites mid-sync.
    /// </remarks>
    public sealed class ChatLocalState
    {
        private string _jid;
        private ChatStatus _status = ChatStatus.Active;
        private bool _isChatPinned;
        private bool _isWidgetPinned;
        private long? _mutedUntil;
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
