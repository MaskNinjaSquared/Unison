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
            set
            {
                _content = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DocumentDisplayName));
            }
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
                RaiseStatusUiChanged();
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
                RaiseStatusUiChanged();
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
                RaiseKindDependentChanged();
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
                Raise(
                    nameof(Kind),
                    nameof(ShowTextContent),
                    nameof(NeedsImageDownload),
                    nameof(CanDownloadImage));
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
                Raise(
                    nameof(Kind),
                    nameof(ShowTextContent),
                    nameof(AudioButtonText));
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
                Raise(
                    nameof(Kind),
                    nameof(IsAudio),
                    nameof(AudioButtonText));
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
            set
            {
                _audioUri = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLocalAudio));
                NotifyAudioDownloadStateChanged();
            }
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

        /// <summary>Voice/audio bubble without a local file yet — show download control.</summary>
        [JsonIgnore]
        public bool NeedsAudioDownload => IsAudio && !HasLocalAudio;

        /// <summary>True when persisted media keys allow an on-demand download.</summary>
        [JsonIgnore]
        public bool CanDownloadAudio =>
            NeedsAudioDownload &&
            !string.IsNullOrWhiteSpace(AudioMediaKeyBase64) &&
            (!string.IsNullOrWhiteSpace(AudioUrl) || !string.IsNullOrWhiteSpace(AudioDirectPath));

        /// <summary>Raises audio download bindable props after keys / local URI change.</summary>
        public void NotifyAudioDownloadStateChanged()
        {
            Raise(
                nameof(NeedsAudioDownload),
                nameof(CanDownloadAudio),
                nameof(HasLocalAudio));
        }

        /// <summary>
        /// Body text is hidden for audio, images, video, stickers, and documents.
        /// </summary>
        [JsonIgnore]
        public bool ShowTextContent => !IsAudio && !IsImage && !IsVideo && !IsSticker && !IsDocument;

        /// <summary>Protocol video message (local file may still be pending download).</summary>
        [JsonIgnore]
        public bool IsVideo => _kind == ChatMessageKind.Video;

        [JsonIgnore]
        public bool IsSticker => _kind == ChatMessageKind.Sticker;

        /// <summary>Protocol document / file message.</summary>
        [JsonIgnore]
        public bool IsDocument => _kind == ChatMessageKind.Document;

        private bool _isStickerFailed;
        /// <summary>Sticker media could not be loaded — bubble shows "!Sticker Error".</summary>
        [JsonIgnore]
        public bool IsStickerFailed
        {
            get => _isStickerFailed;
            set
            {
                if (_isStickerFailed == value) return;
                _isStickerFailed = value;
                OnPropertyChanged();
                Raise(nameof(ShowStickerError), nameof(ShowStickerLoading));
            }
        }

        [JsonIgnore]
        public bool ShowStickerError => IsSticker && IsStickerFailed && !HasImage;

        /// <summary>Sticker row waiting for media (avoids an empty bubble).</summary>
        [JsonIgnore]
        public bool ShowStickerLoading => IsSticker && !HasImage && !IsStickerFailed;

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
            Raise(
                nameof(NeedsImageDownload),
                nameof(CanDownloadImage),
                nameof(ShowTextContent),
                nameof(ShowStickerError),
                nameof(ShowStickerLoading));
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
                Raise(
                    nameof(HasImage),
                    nameof(ShowTextContent),
                    nameof(ShowStickerError),
                    nameof(ShowStickerLoading),
                    nameof(NeedsImageDownload),
                    nameof(CanDownloadImage));
            }
        }

        /// <summary>CDN / MMG URL from protocol (persisted for on-demand download).</summary>
        public string ImageUrl { get; set; }
        public string ImageDirectPath { get; set; }
        public string ImageMediaKeyBase64 { get; set; }
        public string ImageFileEncSha256Base64 { get; set; }
        public string ImageMimeType { get; set; }

        private string _videoUri;
        /// <summary>Local cached video file (ms-appdata) after on-demand download.</summary>
        public string VideoUri
        {
            get => _videoUri;
            set
            {
                _videoUri = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLocalVideo));
                NotifyVideoDownloadStateChanged();
            }
        }

        private string _videoPosterUri;
        /// <summary>Local JPEG poster generated from the first frame after download.</summary>
        public string VideoPosterUri
        {
            get => _videoPosterUri;
            set
            {
                _videoPosterUri = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasVideoPoster));
            }
        }

        public string VideoUrl { get; set; }
        public string VideoDirectPath { get; set; }
        public string VideoMediaKeyBase64 { get; set; }
        public string VideoFileEncSha256Base64 { get; set; }
        public string VideoMimeType { get; set; }

        private uint _videoDurationSeconds;
        public uint VideoDurationSeconds
        {
            get => _videoDurationSeconds;
            set { _videoDurationSeconds = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public bool HasLocalVideo => !string.IsNullOrWhiteSpace(VideoUri);

        [JsonIgnore]
        public bool HasVideoPoster => !string.IsNullOrWhiteSpace(VideoPosterUri);

        /// <summary>Video protocol message without a local file yet — show download placeholder.</summary>
        [JsonIgnore]
        public bool NeedsVideoDownload => IsVideo && !HasLocalVideo;

        /// <summary>True when persisted media keys allow an on-demand video download.</summary>
        [JsonIgnore]
        public bool CanDownloadVideo =>
            NeedsVideoDownload &&
            !string.IsNullOrWhiteSpace(VideoMediaKeyBase64) &&
            (!string.IsNullOrWhiteSpace(VideoUrl) || !string.IsNullOrWhiteSpace(VideoDirectPath));

        /// <summary>Raises video download-related bindable props after keys / local URI change.</summary>
        public void NotifyVideoDownloadStateChanged()
        {
            Raise(
                nameof(NeedsVideoDownload),
                nameof(CanDownloadVideo),
                nameof(HasLocalVideo),
                nameof(ShowTextContent),
                nameof(IsVideo));
        }

        private string _documentFileName;
        /// <summary>Original file name from the protocol DocumentMessage.</summary>
        public string DocumentFileName
        {
            get => _documentFileName;
            set
            {
                if (string.Equals(_documentFileName, value, StringComparison.Ordinal)) return;
                _documentFileName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DocumentDisplayName));
            }
        }

        /// <summary>UI label: file name (protocol or content), or a generic "Document" fallback.</summary>
        [JsonIgnore]
        public string DocumentDisplayName
        {
            get
            {
                string resolved = ChatPreviewNormalizer.ResolveDocumentDisplayName(DocumentFileName, Content);
                return !string.IsNullOrWhiteSpace(resolved) ? resolved : "Document";
            }
        }

        private string _documentUri;
        /// <summary>Local cached document file (ms-appdata) after on-demand download.</summary>
        public string DocumentUri
        {
            get => _documentUri;
            set
            {
                _documentUri = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLocalDocument));
                NotifyDocumentDownloadStateChanged();
            }
        }

        public string DocumentUrl { get; set; }
        public string DocumentDirectPath { get; set; }
        public string DocumentMediaKeyBase64 { get; set; }
        public string DocumentFileEncSha256Base64 { get; set; }
        public string DocumentMimeType { get; set; }

        private long _documentFileLengthBytes;
        /// <summary>Original file size in bytes from protocol (or filled after local download).</summary>
        public long DocumentFileLengthBytes
        {
            get => _documentFileLengthBytes;
            set
            {
                if (_documentFileLengthBytes == value) return;
                _documentFileLengthBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDocumentFileSize));
            }
        }

        [JsonIgnore]
        public bool HasDocumentFileSize => DocumentFileLengthBytes > 0;

        [JsonIgnore]
        public bool HasLocalDocument => !string.IsNullOrWhiteSpace(DocumentUri);

        /// <summary>Document protocol message without a local file yet.</summary>
        [JsonIgnore]
        public bool NeedsDocumentDownload => IsDocument && !HasLocalDocument;

        /// <summary>True when persisted media keys allow an on-demand document download.</summary>
        [JsonIgnore]
        public bool CanDownloadDocument =>
            NeedsDocumentDownload &&
            !string.IsNullOrWhiteSpace(DocumentMediaKeyBase64) &&
            (!string.IsNullOrWhiteSpace(DocumentUrl) || !string.IsNullOrWhiteSpace(DocumentDirectPath));

        public void NotifyDocumentDownloadStateChanged()
        {
            Raise(
                nameof(NeedsDocumentDownload),
                nameof(CanDownloadDocument),
                nameof(HasLocalDocument),
                nameof(HasDocumentFileSize),
                nameof(ShowTextContent),
                nameof(IsDocument),
                nameof(DocumentDisplayName));
        }

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
            Raise(
                nameof(Reactions),
                nameof(TotalReactions),
                nameof(HasReactions),
                nameof(ReactionsDisplayText),
                nameof(ReactionChips));
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

        private string _quotedText;
        public string QuotedText
        {
            get => _quotedText;
            set
            {
                if (_quotedText == value) return;
                _quotedText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasQuote));
            }
        }

        private ChatPreviewKind _quotedKind;
        /// <summary>Quoted media type for the reply strip (icon + label); Text = body-only quote.</summary>
        public ChatPreviewKind QuotedKind
        {
            get => _quotedKind;
            set
            {
                if (_quotedKind == value) return;
                _quotedKind = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasQuote));
            }
        }

        private string _quotedSenderName;
        public string QuotedSenderName
        {
            get => _quotedSenderName;
            set
            {
                if (_quotedSenderName == value) return;
                _quotedSenderName = value;
                OnPropertyChanged();
            }
        }

        private string _quotedMessageId;
        /// <summary>Id of the quoted message (<c>ContextInfo.StanzaId</c>) when available.</summary>
        public string QuotedMessageId
        {
            get => _quotedMessageId;
            set
            {
                if (string.Equals(_quotedMessageId, value, StringComparison.Ordinal)) return;
                _quotedMessageId = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public bool HasQuote =>
            _quotedKind != ChatPreviewKind.Text || !string.IsNullOrWhiteSpace(_quotedText);

        private List<string> _mentionedJids;
        /// <summary>JIDs from ContextInfo.MentionedJid (for @number → display name).</summary>
        public List<string> MentionedJids
        {
            get => _mentionedJids ?? (_mentionedJids = new List<string>());
            set
            {
                _mentionedJids = value;
                OnPropertyChanged();
            }
        }

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

        /// <summary>
        /// Ephemeral bubble synthesized from chat-list preview when history is missing.
        /// Never persist; remove when real messages arrive.
        /// </summary>
        [JsonIgnore]
        public bool IsPreviewFallback { get; set; }

        private string _contactUri;
        /// <summary>
        /// Group author avatar URI (resolved from the participant's ChatItem.AvatarUrl).
        /// </summary>
        [JsonIgnore]
        public string ContactUri
        {
            get => _contactUri;
            set
            {
                if (string.Equals(_contactUri, value, StringComparison.Ordinal)) return;
                _contactUri = value;
                OnPropertyChanged();
            }
        }

        private bool _showContact;
        /// <summary>
        /// When true, show the author avatar beside the bubble (first message of a participant run).
        /// </summary>
        [JsonIgnore]
        public bool ShowContact
        {
            get => _showContact;
            set
            {
                if (_showContact == value) return;
                _showContact = value;
                OnPropertyChanged();
            }
        }

        private bool _showContactSlot;
        /// <summary>
        /// Reserve left gutter for group-author avatars so consecutive bubbles stay aligned.
        /// </summary>
        [JsonIgnore]
        public bool ShowContactSlot
        {
            get => _showContactSlot;
            set
            {
                if (_showContactSlot == value) return;
                _showContactSlot = value;
                OnPropertyChanged();
            }
        }

        public string FormattedTime => Timestamp == DateTime.MinValue ? string.Empty : Timestamp.ToString("HH:mm");

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

        private void RaiseStatusUiChanged()
        {
            Raise(
                nameof(StatusGlyph),
                nameof(StatusCheckmarkUri),
                nameof(HasStatusCheckmark),
                nameof(IsReadStatus),
                nameof(IsSendFailed));
        }

        private void RaiseKindDependentChanged()
        {
            Raise(
                nameof(IsImage),
                nameof(IsVideo),
                nameof(IsAudio),
                nameof(IsVoiceMessage),
                nameof(IsSticker),
                nameof(IsDocument),
                nameof(ShowTextContent),
                nameof(ShowStickerError),
                nameof(ShowStickerLoading),
                nameof(NeedsImageDownload),
                nameof(CanDownloadImage),
                nameof(NeedsVideoDownload),
                nameof(CanDownloadVideo),
                nameof(NeedsDocumentDownload),
                nameof(CanDownloadDocument),
                nameof(AudioButtonText));
        }
    }
}
