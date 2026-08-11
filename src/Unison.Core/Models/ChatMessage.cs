using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    public class ChatMessage : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _id;
        public string Id 
        { 
            get => _id; 
            set { _id = value; OnPropertyChanged(); } 
        }

        private string _content;
        public string Content 
        { 
            get => _content; 
            set { _content = value; OnPropertyChanged(); } 
        }

        private DateTime _timestamp;
        public DateTime Timestamp 
        { 
            get => _timestamp; 
            set { _timestamp = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedTime)); } 
        }

        private bool _isFromMe;
        public bool IsFromMe 
        { 
            get => _isFromMe; 
            set
            {
                if (_isFromMe == value) return;
                _isFromMe = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusGlyph));
                OnPropertyChanged(nameof(StatusCheckmarkUri));
                OnPropertyChanged(nameof(HasStatusCheckmark));
                OnPropertyChanged(nameof(IsReadStatus));
                OnPropertyChanged(nameof(IsSendFailed));
            } 
        }

        public const string StatusPending = "pending";
        public const string StatusSent = "sent";
        public const string StatusDelivered = "delivered";
        public const string StatusRead = "read";
        public const string StatusFailed = "failed";

        private string _status;
        public string Status
        {
            get => _status;
            set
            {
                if (string.Equals(_status, value, StringComparison.OrdinalIgnoreCase)) return;
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusGlyph));
                OnPropertyChanged(nameof(StatusCheckmarkUri));
                OnPropertyChanged(nameof(HasStatusCheckmark));
                OnPropertyChanged(nameof(IsReadStatus));
                OnPropertyChanged(nameof(IsSendFailed));
            }
        }

        [JsonIgnore]
        public string StatusGlyph
        {
            get
            {
                // Kept for callers that still expect a glyph string; UI uses StatusCheckmarkUri.
                if (!IsFromMe || IsSendFailed) return string.Empty;
                switch ((Status ?? string.Empty).ToLowerInvariant())
                {
                    case StatusPending: return "?";
                    case StatusDelivered: return "??";
                    case StatusRead: return "??";
                    case StatusSent:
                    default: return "?";
                }
            }
        }

        /// <summary>
        /// Asset URI for outgoing message ticks (sent / delivered / read).
        /// </summary>
        [JsonIgnore]
        public string StatusCheckmarkUri
        {
            get
            {
                if (!IsFromMe || IsSendFailed)
                {
                    return null;
                }

                switch ((Status ?? string.Empty).ToLowerInvariant())
                {
                    case StatusRead:
                        return "ms-appx:///Assets/Checkmarks/read_message.png";
                    case StatusDelivered:
                        return "ms-appx:///Assets/Checkmarks/delivered_message.png";
                    case StatusPending:
                    case StatusSent:
                    default:
                        // Pending uses the single-tick asset until server ack arrives.
                        return "ms-appx:///Assets/Checkmarks/sent_message.png";
                }
            }
        }

        [JsonIgnore]
        public bool HasStatusCheckmark => !string.IsNullOrEmpty(StatusCheckmarkUri);

        [JsonIgnore]
        public bool IsReadStatus => string.Equals(Status, StatusRead, StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsSendFailed => string.Equals(Status, StatusFailed, StringComparison.OrdinalIgnoreCase);

        private string _senderName;
        public string SenderName 
        { 
            get => _senderName; 
            set
            {
                if (string.Equals(_senderName, value, StringComparison.Ordinal)) return;
                _senderName = value;
                OnPropertyChanged();
            }
        }

        private ChatMessageKind _kind;
        /// <summary>
        /// Protocol-derived type. Prefer this over parsing <see cref="Content"/>.
        /// Legacy rows may only have <see cref="IsImage"/> / audio flags — call
        /// <see cref="EnsureKindFromLegacyFlags"/> after deserialize if needed.
        /// </summary>
        public ChatMessageKind Kind
        {
            get => _kind;
            set
            {
                if (_kind == value) return;
                _kind = value;
                SyncLegacyFlagsFromKind();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(IsAudio));
                OnPropertyChanged(nameof(IsVoiceMessage));
                OnPropertyChanged(nameof(ShowTextContent));
                OnPropertyChanged(nameof(NeedsImageDownload));
                OnPropertyChanged(nameof(CanDownloadImage));
                OnPropertyChanged(nameof(AudioButtonText));
            }
        }

        private bool _isImage;
        public bool IsImage
        {
            get => _isImage || _kind == ChatMessageKind.Image;
            set
            {
                if (_isImage == value && (!value || _kind == ChatMessageKind.Image)) return;
                _isImage = value;
                if (value)
                {
                    _kind = ChatMessageKind.Image;
                }
                else if (_kind == ChatMessageKind.Image)
                {
                    _kind = ChatMessageKind.Text;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(Kind));
                OnPropertyChanged(nameof(ShowTextContent));
                OnPropertyChanged(nameof(NeedsImageDownload));
                OnPropertyChanged(nameof(CanDownloadImage));
            }
        }


        private bool _isAudio;
        public bool IsAudio
        {
            get => _isAudio || _kind == ChatMessageKind.Audio || _kind == ChatMessageKind.Voice;
            set
            {
                if (_isAudio == value) return;
                _isAudio = value;
                if (value)
                {
                    if (_kind != ChatMessageKind.Voice)
                    {
                        _kind = ChatMessageKind.Audio;
                    }
                }
                else if (_kind == ChatMessageKind.Audio || _kind == ChatMessageKind.Voice)
                {
                    _kind = ChatMessageKind.Text;
                    _isVoiceMessage = false;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(Kind));
                OnPropertyChanged(nameof(ShowTextContent));
                OnPropertyChanged(nameof(AudioButtonText));
            }
        }

        private bool _isVoiceMessage;
        public bool IsVoiceMessage
        {
            get => _isVoiceMessage || _kind == ChatMessageKind.Voice;
            set
            {
                if (_isVoiceMessage == value && (!value || _kind == ChatMessageKind.Voice)) return;
                _isVoiceMessage = value;
                if (value)
                {
                    _isAudio = true;
                    _kind = ChatMessageKind.Voice;
                }
                else if (_kind == ChatMessageKind.Voice)
                {
                    _kind = _isAudio ? ChatMessageKind.Audio : ChatMessageKind.Text;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(Kind));
                OnPropertyChanged(nameof(IsAudio));
                OnPropertyChanged(nameof(AudioButtonText));
            }
        }

        /// <summary>
        /// Fills <see cref="Kind"/> from legacy boolean flags after JSON load
        /// (rows persisted before Kind existed).
        /// </summary>
        public void EnsureKindFromLegacyFlags()
        {
            if (_kind != ChatMessageKind.Text)
            {
                return;
            }

            ChatMessageKind inferred = ChatPreviewNormalizer.ResolveKind(
                _isImage,
                false,
                false,
                _isAudio,
                _isVoiceMessage);

            if (inferred == ChatMessageKind.Text)
            {
                return;
            }

            _kind = inferred;
            SyncLegacyFlagsFromKind();
        }

        private void SyncLegacyFlagsFromKind()
        {
            _isImage = _kind == ChatMessageKind.Image;
            _isVoiceMessage = _kind == ChatMessageKind.Voice;
            _isAudio = _kind == ChatMessageKind.Audio || _kind == ChatMessageKind.Voice;
        }

        private string _audioUri;
        public string AudioUri
        {
            get => _audioUri;
            set { _audioUri = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLocalAudio)); }
        }

        public string AudioUrl { get; set; }
        public string AudioDirectPath { get; set; }
        public string AudioMediaKeyBase64 { get; set; }
        public string AudioFileEncSha256Base64 { get; set; }
        public string AudioMimeType { get; set; }

        private uint _audioDurationSeconds;
        public uint AudioDurationSeconds
        {
            get => _audioDurationSeconds;
            set { _audioDurationSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(AudioButtonText)); }
        }

        [JsonIgnore]
        public bool HasLocalAudio => !string.IsNullOrWhiteSpace(AudioUri);

        /// <summary>
        /// Body text is hidden for audio and for image kinds (downloaded or pending download).
        /// </summary>
        [JsonIgnore]
        public bool ShowTextContent => !IsAudio && !IsImage;

        /// <summary>Image protocol message without a local file yet — show download placeholder.</summary>
        [JsonIgnore]
        public bool NeedsImageDownload => IsImage && !HasImage;

        /// <summary>True when persisted media keys allow an on-demand download.</summary>
        [JsonIgnore]
        public bool CanDownloadImage =>
            NeedsImageDownload && !string.IsNullOrWhiteSpace(ImageMediaKeyBase64);

        /// <summary>Raises download-related bindable props after media keys are assigned.</summary>
        public void NotifyImageDownloadStateChanged()
        {
            OnPropertyChanged(nameof(NeedsImageDownload));
            OnPropertyChanged(nameof(CanDownloadImage));
            OnPropertyChanged(nameof(ShowTextContent));
        }

        [JsonIgnore]
        public string AudioButtonText
        {
            get
            {
                // UI binds ChatMessageViewModel.AudioButtonText (localized).
                // Model fallback stays English for non-UI / legacy callers.
                string kind = IsVoiceMessage ? "Voice message" : "Audio";
                if (AudioDurationSeconds == 0) return kind;
                return string.Format("{0}  {1}:{2:00}", kind, AudioDurationSeconds / 60, AudioDurationSeconds % 60);
            }
        }

        private string _imageUri;
        public string ImageUri
        {
            get => _imageUri;
            set
            {
                _imageUri = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(ShowTextContent));
                OnPropertyChanged(nameof(NeedsImageDownload));
                OnPropertyChanged(nameof(CanDownloadImage));
            }
        }

        /// <summary>CDN / MMG URL from protocol (persisted for on-demand download).</summary>
        public string ImageUrl { get; set; }
        public string ImageDirectPath { get; set; }
        public string ImageMediaKeyBase64 { get; set; }
        public string ImageFileEncSha256Base64 { get; set; }
        public string ImageMimeType { get; set; }

        private string _caption;
        public string Caption
        {
            get => _caption;
            set { _caption = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCaption)); }
        }

        private string _remoteJid;
        public string RemoteJid
        {
            get => _remoteJid;
            set
            {
                if (string.Equals(_remoteJid, value, StringComparison.Ordinal)) return;
                _remoteJid = value;
                OnPropertyChanged();
            }
        }

        private string _participantJid;
        public string ParticipantJid
        {
            get => _participantJid;
            set { _participantJid = value; OnPropertyChanged(); }
        }

        private bool _isPinned;
        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isPinned == value) return;
                _isPinned = value;
                OnPropertyChanged();
            }
        }

        private DateTime? _pinnedAtUtc;
        public DateTime? PinnedAtUtc
        {
            get => _pinnedAtUtc;
            set { _pinnedAtUtc = value; OnPropertyChanged(); }
        }

        private DateTime? _pinExpiresAtUtc;
        public DateTime? PinExpiresAtUtc
        {
            get => _pinExpiresAtUtc;
            set { _pinExpiresAtUtc = value; OnPropertyChanged(); }
        }

        private List<MessageReaction> _reactions;
        /// <summary>
        /// Reactions attached to this message (not timeline rows). Persisted in JSON.
        /// </summary>
        public List<MessageReaction> Reactions
        {
            get { return _reactions ?? (_reactions = new List<MessageReaction>()); }
            set
            {
                _reactions = value ?? new List<MessageReaction>();
                NotifyReactionsChanged();
            }
        }

        /// <summary>Count of <see cref="Reactions"/> (all reactors, not unique emojis).</summary>
        [JsonIgnore]
        public int TotalReactions
        {
            get { return _reactions == null ? 0 : _reactions.Count; }
        }

        [JsonIgnore]
        public bool HasReactions
        {
            get { return TotalReactions > 0; }
        }

        /// <summary>Side-by-side emoji line from <see cref="ReactionsBuilder.BuildEmojiLine"/>.</summary>
        [JsonIgnore]
        public string ReactionsDisplayText
        {
            get { return ReactionsBuilder.BuildEmojiLine(Reactions); }
        }

        /// <summary>Grouped chips for rounded buttons under the bubble.</summary>
        [JsonIgnore]
        public IList<ReactionChip> ReactionChips
        {
            get { return ReactionsBuilder.BuildChips(Reactions); }
        }

        /// <summary>Call after in-place mutate of <see cref="Reactions"/> (upsert/remove).</summary>
        public void NotifyReactionsChanged()
        {
            OnPropertyChanged(nameof(Reactions));
            OnPropertyChanged(nameof(TotalReactions));
            OnPropertyChanged(nameof(HasReactions));
            OnPropertyChanged(nameof(ReactionsDisplayText));
            OnPropertyChanged(nameof(ReactionChips));
        }

        private bool _isRunStart = true;
        public bool IsRunStart
        {
            get => _isRunStart;
            set
            {
                if (_isRunStart == value) return;
                _isRunStart = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowTail));
            }
        }

        private bool _isRunEnd = true;
        public bool IsRunEnd
        {
            get => _isRunEnd;
            set
            {
                if (_isRunEnd == value) return;
                _isRunEnd = value;
                OnPropertyChanged();
            }
        }

        public bool HasImage => !string.IsNullOrWhiteSpace(ImageUri);
        public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);
        public bool ShowTail => IsRunStart;

        private bool _showGroupSenderName;
        /// <summary>
        /// Group received bubbles: show the participant name only on the first bubble of a run.
        /// Set by the chat detail view when recomputing runs (not inferred from RemoteJid alone).
        /// </summary>
        [JsonIgnore]
        public bool ShowGroupSenderName
        {
            get => _showGroupSenderName;
            set
            {
                if (_showGroupSenderName == value) return;
                _showGroupSenderName = value;
                OnPropertyChanged();
            }
        }

        public string FormattedTime => Timestamp == DateTime.MinValue ? string.Empty : Timestamp.ToString("HH:mm");

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
