using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// One timeline bubble: presentation state + per-message actions (media prepare, document, pin).
    /// Conversation-level composer/send stays on <see cref="ChatDetailViewModel"/>.
    /// </summary>
    public partial class ChatMessageViewModel : INotifyPropertyChanged, IGifPlaybackClient
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Collapsed body line cap (UWP <c>TextBlock.MaxLines</c>).</summary>
        public const int CollapsedContentMaxLines = 12;

        private readonly IStringResources _strings;
        private readonly IMessageService _messages;
        private readonly IDialogService _dialogs;
        private readonly IUriLauncher _uriLauncher;
        private readonly IFilePicker _filePicker;
        private readonly IAudioExportService _audioExport;
        private readonly ISessionLogger _sessionLogger;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly IHistoryMessageStore _history;
        private readonly ICopyService _copy;
        private readonly IDispatcher _ui;
        private readonly DeviceCategory _deviceCategory;
        private readonly IGifPlaybackCoordinator _gifPlayback;
        private readonly IContactService _contacts;
        private readonly IChatService _chats;
        private readonly IPersonStore _people;
        private readonly Action<string> _openChat;
        private bool _gifRegistered;
        private bool _hasStartedGifPlayback;
        private bool _isGifPlaying;
        private bool _gifAutoDownloadStarted;
        private bool _gifAutoDownloadFailed;
        private Task _gifAutoDownloadTask;
        private bool _isExpanded;
        private bool _isHighlighted;
        private bool? _canExpandCached;
        private string _quotedStripTextCached;
        private ChatPreviewKind? _quotedPreviewKindCached;
        private string _documentDisplayNameCached;

        public ChatMessage Model { get; }

        public ChatMessageViewModel(
            ChatMessage model,
            IStringResources strings = null,
            IMessageService messages = null,
            IDialogService dialogs = null,
            IUriLauncher uriLauncher = null,
            IFilePicker filePicker = null,
            IAudioExportService audioExport = null,
            ISessionLogger sessionLogger = null,
            IRuntimeDiagnostics diagnostics = null,
            IHistoryMessageStore history = null,
            ICopyService copy = null,
            IDispatcher ui = null,
            DeviceCategory deviceCategory = DeviceCategory.High,
            IGifPlaybackCoordinator gifPlayback = null,
            IContactService contacts = null,
            IChatService chats = null,
            IPersonStore people = null,
            Action<string> openChat = null)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            _strings = strings;
            _messages = messages;
            _dialogs = dialogs;
            _uriLauncher = uriLauncher;
            _filePicker = filePicker;
            _audioExport = audioExport;
            _sessionLogger = sessionLogger;
            _diagnostics = diagnostics;
            _history = history;
            _copy = copy;
            // Pump / revoke / reaction mutate ChatMessage off the UI thread. Forwarding
            // PropertyChanged inline hits XAML bindings (IsRevoked, HasReactions) and throws
            // RPC_E_WRONG_THREAD (0x8001010E) on Mobile — 1.8.3 avoided IsRevoked on tombstone.
            _ui = ui;
            _deviceCategory = deviceCategory;
            _gifPlayback = gifPlayback;
            _contacts = contacts;
            _chats = chats;
            _people = people;
            _openChat = openChat;

            ShowReactionsCommand = new RelayCommand(
                () => _ = ShowReactionsAsync(),
                () => HasReactions && _dialogs != null);

            ToggleExpandCommand = new RelayCommand(
                () => IsExpanded = !IsExpanded,
                () => CanExpand);

            DownloadAudioCommand = new RelayCommand(
                () => _ = DownloadAudioAsync(),
                () => Model.IsAudio && (NeedsAudioDownload || AudioPlaybackStatus == AudioPlaybackStatus.NotAvailable));

            DownloadImageCommand = new RelayCommand(
                () => _ = DownloadImageAsync(),
                () => NeedsImageDownload && !IsImageDownloading);

            DownloadVideoCommand = new RelayCommand(
                () => _ = DownloadVideoAsync(),
                () => NeedsVideoDownload && !IsVideoDownloading);

            DocumentPrimaryCommand = new RelayCommand(
                () => _ = DocumentPrimaryAsync(),
                () => Model.IsDocument && !IsDocumentDownloading);

            DownloadDocumentCommand = new RelayCommand(
                () => _ = DownloadDocumentAsync(confirmFirst: false),
                () => NeedsDocumentDownload && !IsDocumentDownloading);

            OpenDocumentCommand = new RelayCommand(
                () => _ = OpenDocumentAsync(),
                () => HasLocalDocument && !IsDocumentDownloading);

            SaveDocumentAsCommand = new RelayCommand(
                () => _ = SaveDocumentAsAsync(),
                () => HasLocalDocument);

            SaveAudioAsCommand = new RelayCommand(
                () => _ = SaveAudioAsAsync(),
                () => CanSaveAudioAs);

            ExportDocumentCommand = new RelayCommand(
                () => _ = ExportDocumentAsync(),
                () => Model.IsDocument && !IsDocumentDownloading);

            MessageContactChatCommand = new RelayCommand(
                MessageContactChat,
                () => CanMessageContact);

            MessageContactAddCommand = new RelayCommand(
                () => _ = MessageContactAddAsync(),
                () => CanAddSharedContact);

            CopyCommand = new RelayCommand(CopyText, () => !string.IsNullOrWhiteSpace(CopyableText));
            ReplyCommand = new RelayCommand(() => ReplyRequested?.Invoke(this, EventArgs.Empty));

            PlayGifCommand = new RelayCommand(
                StartGifPlayback,
                () => ShowGifSurface && !IsGifPlaying);

            Model.PropertyChanged += OnModelPropertyChanged;
            SyncAudioPlaybackStatusFromModel();
            EnsureGifPlaybackRegistration();
            ScheduleGifAutoDownloadIfNeeded();
            EnsureContactCardPhoto();
        }

        /// <summary>User tapped the GIF glyph — play in-bubble only (no fullscreen viewer).</summary>
        public ICommand PlayGifCommand { get; }

        /// <summary>Opens (or starts) a 1:1 chat with the shared contact.</summary>
        public ICommand MessageContactChatCommand { get; }

        /// <summary>Adds the shared contact to the device address book when not already saved.</summary>
        public ICommand MessageContactAddCommand { get; }

        /// <summary>
        /// Drops the model subscription so cleared/trimmed timeline rows can be GC'd.
        /// Call before removing from any collection that owns this bubble.
        /// </summary>
        public void Detach()
        {
            Model.PropertyChanged -= OnModelPropertyChanged;
            UnregisterGifPlayback();
        }

        /// <summary>
        /// Forwards model changes and fans out only the VM-computed props that depend on each group.
        /// Marshalled onto the UI dispatcher when one is supplied (incoming pump is not UI-bound).
        /// </summary>
        private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            PostToUi(() => ApplyModelPropertyChanged(e));
        }

        private void ApplyModelPropertyChanged(PropertyChangedEventArgs e)
        {
            string name = e?.PropertyName;
            PropertyChanged?.Invoke(this, e);

            if (IsReactionProperty(name))
            {
                (ShowReactionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }

            if (IsExpandInputProperty(name))
            {
                _canExpandCached = null;
                RaiseExpandPropsChanged();
            }

            if (IsQuoteProperty(name))
            {
                _quotedStripTextCached = null;
                _quotedPreviewKindCached = null;
                RaiseQuoteUiChanged();
            }

            if (name == nameof(ChatMessage.DocumentFileName) ||
                name == nameof(ChatMessage.Content) ||
                name == nameof(ChatMessage.DocumentDisplayName))
            {
                _documentDisplayNameCached = null;
            }

            if (name == nameof(ChatMessage.Content) ||
                name == nameof(ChatMessage.Caption) ||
                name == nameof(ChatMessage.Kind))
            {
                (CopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }

            if (name == nameof(ChatMessage.MentionedJids))
            {
                RaiseCore(nameof(MentionedJids));
            }

            if (name == nameof(ChatMessage.IsForwarded))
            {
                RaiseCore(nameof(IsForwarded));
                RaiseCore(nameof(ShowForwardedLabel));
                RaiseCore(nameof(ForwardedLabelText));
            }

            if (name == nameof(ChatMessage.ShowQuotedAuthorLink))
            {
                RaiseCore(nameof(ShowQuotedAuthorLink));
                RaiseCore(nameof(CanOpenQuotedAuthor));
            }

            if (IsImageOrStickerProperty(name))
            {
                RaiseImageStickerUiChanged();
            }

            if (IsVideoProperty(name))
            {
                RaiseVideoUiChanged();
            }

            if (IsDocumentProperty(name))
            {
                RaiseDocumentUiChanged();
            }

            if (IsContactProperty(name))
            {
                RaiseContactUiChanged();
            }

            if (IsAudioProperty(name))
            {
                RaiseAudioModelUiChanged();
            }
        }

        private void PostToUi(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (_ui == null)
            {
                action();
                return;
            }

            _ = _ui.RunAsync(action);
        }

        private static bool IsReactionProperty(string name) =>
            name == nameof(ChatMessage.HasReactions) ||
            name == nameof(ChatMessage.TotalReactions) ||
            name == nameof(ChatMessage.ReactionsDisplayText) ||
            name == nameof(ChatMessage.Reactions);

        private static bool IsExpandInputProperty(string name) =>
            name == nameof(ChatMessage.Content) ||
            name == nameof(ChatMessage.ShowTextContent) ||
            name == nameof(ChatMessage.IsAudio) ||
            name == nameof(ChatMessage.ImageUri) ||
            name == nameof(ChatMessage.NeedsImageDownload) ||
            name == nameof(ChatMessage.Kind) ||
            name == nameof(ChatMessage.QuotedText) ||
            name == nameof(ChatMessage.QuotedKind) ||
            name == nameof(ChatMessage.QuotedSenderName) ||
            name == nameof(ChatMessage.QuotedMessageId) ||
            name == nameof(ChatMessage.MentionedJids);

        private static bool IsQuoteProperty(string name) =>
            name == nameof(ChatMessage.QuotedText) ||
            name == nameof(ChatMessage.QuotedKind) ||
            name == nameof(ChatMessage.QuotedSenderName) ||
            name == nameof(ChatMessage.QuotedParticipantJid) ||
            name == nameof(ChatMessage.QuotedMessageId);

        private static bool IsImageOrStickerProperty(string name) =>
            name == nameof(ChatMessage.NeedsImageDownload) ||
            name == nameof(ChatMessage.HasImage) ||
            name == nameof(ChatMessage.ImageUri) ||
            name == nameof(ChatMessage.ThumbnailUri) ||
            name == nameof(ChatMessage.HasMediaThumbnail) ||
            name == nameof(ChatMessage.IsImage) ||
            name == nameof(ChatMessage.ShowStickerError) ||
            name == nameof(ChatMessage.ShowStickerLoading) ||
            name == nameof(ChatMessage.IsStickerFailed) ||
            name == nameof(ChatMessage.IsSticker);

        private static bool IsVideoProperty(string name) =>
            name == nameof(ChatMessage.NeedsVideoDownload) ||
            name == nameof(ChatMessage.HasLocalVideo) ||
            name == nameof(ChatMessage.HasVideoPoster) ||
            name == nameof(ChatMessage.VideoUri) ||
            name == nameof(ChatMessage.VideoPosterUri) ||
            name == nameof(ChatMessage.VideoDurationSeconds) ||
            name == nameof(ChatMessage.CanDownloadVideo) ||
            name == nameof(ChatMessage.IsVideo) ||
            name == nameof(ChatMessage.IsGif) ||
            name == nameof(ChatMessage.IsAnimated) ||
            name == nameof(ChatMessage.Kind);

        private static bool IsDocumentProperty(string name) =>
            name == nameof(ChatMessage.NeedsDocumentDownload) ||
            name == nameof(ChatMessage.CanDownloadDocument) ||
            name == nameof(ChatMessage.HasLocalDocument) ||
            name == nameof(ChatMessage.DocumentUri) ||
            name == nameof(ChatMessage.DocumentFileName) ||
            name == nameof(ChatMessage.DocumentDisplayName) ||
            name == nameof(ChatMessage.DocumentFileLengthBytes) ||
            name == nameof(ChatMessage.HasDocumentFileSize) ||
            name == nameof(ChatMessage.Content) ||
            name == nameof(ChatMessage.IsDocument) ||
            name == nameof(ChatMessage.Kind);

        private static bool IsContactProperty(string name) =>
            name == nameof(ChatMessage.IsContact) ||
            name == nameof(ChatMessage.ContactCardDisplayName) ||
            name == nameof(ChatMessage.ContactCardJid) ||
            name == nameof(ChatMessage.ContactCardPhone) ||
            name == nameof(ChatMessage.ContactCardPhotoUri) ||
            name == nameof(ChatMessage.HasContactCardPhoto) ||
            name == nameof(ChatMessage.Kind);

        private static bool IsAudioProperty(string name) =>
            name == nameof(ChatMessage.NeedsAudioDownload) ||
            name == nameof(ChatMessage.CanDownloadAudio) ||
            name == nameof(ChatMessage.HasLocalAudio) ||
            name == nameof(ChatMessage.AudioUri) ||
            name == nameof(ChatMessage.IsAudio) ||
            name == nameof(ChatMessage.AudioButtonText) ||
            name == nameof(ChatMessage.AudioDurationSeconds);

        /// <summary>Shows the reaction picker / reaction details for this message.</summary>
        public ICommand ShowReactionsCommand { get; }

        /// <summary>Expands or collapses long message body text in the bubble.</summary>
        public ICommand ToggleExpandCommand { get; }

        /// <summary>Fetches audio media for this bubble (no autoplay).</summary>
        public ICommand DownloadAudioCommand { get; }

        /// <summary>Fetches image media for this bubble.</summary>
        public ICommand DownloadImageCommand { get; }

        /// <summary>Fetches video media for this bubble.</summary>
        public ICommand DownloadVideoCommand { get; }

        /// <summary>Open when ready; otherwise confirm + download.</summary>
        public ICommand DocumentPrimaryCommand { get; }

        /// <summary>Download document without confirm dialog.</summary>
        public ICommand DownloadDocumentCommand { get; }

        /// <summary>Open a locally cached document.</summary>
        public ICommand OpenDocumentCommand { get; }

        /// <summary>FileSavePicker for a cached document.</summary>
        public ICommand SaveDocumentAsCommand { get; }

        /// <summary>Save downloaded audio as MP3 (overflow menu).</summary>
        public ICommand SaveAudioAsCommand { get; }

        /// <summary>Copies this bubble's text, or its caption, to the clipboard.</summary>
        public ICommand CopyCommand { get; }

        /// <summary>Reply to this bubble. The conversation opens the composer bar.</summary>
        public ICommand ReplyCommand { get; }

        public event EventHandler ReplyRequested;

        /// <summary>Download to cache if needed, then FileSavePicker to disk.</summary>
        public ICommand ExportDocumentCommand { get; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                RaiseExpandPropsChanged();
            }
        }

        /// <summary>True when body text is long enough to warrant collapse / read-more.</summary>
        public bool CanExpand
        {
            get
            {
                if (!_canExpandCached.HasValue)
                {
                    _canExpandCached = ShowTextContent && NeedsCollapse(Content, CollapsedContentMaxLines);
                }

                return _canExpandCached.Value;
            }
        }

        /// <summary>0 = unlimited when expanded; otherwise <see cref="CollapsedContentMaxLines"/>.</summary>
        public int ContentMaxLines =>
            IsExpanded || !CanExpand ? 0 : CollapsedContentMaxLines;

        public string ExpandLinkText
        {
            get
            {
                if (IsExpanded)
                {
                    return _strings != null
                        ? _strings.Get("ChatDetail_ReadLess", "Read less")
                        : "Read less";
                }

                return _strings != null
                    ? _strings.Get("ChatDetail_ReadMore", "Read more")
                    : "Read more";
            }
        }

        public string Id => Model.Id;
        public string Content => Model.Content;
        public DateTime Timestamp => Model.Timestamp;
        public bool IsFromMe => Model.IsFromMe;
        public string Status
        {
            get => Model.Status;
            set => Model.Status = value;
        }

        public string StatusCheckmarkUri => Model.StatusCheckmarkUri;
        public string StatusGlyph => Model.StatusGlyph;
        public bool HasStatusCheckmark => Model.HasStatusCheckmark;
        public bool IsReadStatus => Model.IsReadStatus;
        public bool ShowReadStatusCheckmark => Model.ShowReadStatusCheckmark;
        public bool ShowUnreadStatusCheckmark => Model.ShowUnreadStatusCheckmark;
        public bool IsSendFailed => Model.IsSendFailed;
        public string SenderName => Model.SenderName;
        public ChatMessageKind Kind => Model.Kind;
        public bool IsAudio => Model.IsAudio;

        private AudioPlaybackStatus _audioPlaybackStatus = AudioPlaybackStatus.NotDownloaded;
        private uint _audioPlaybackPositionSeconds;

        /// <summary>Download / play / pause / unavailable state for this bubble.</summary>
        public AudioPlaybackStatus AudioPlaybackStatus
        {
            get => _audioPlaybackStatus;
            set
            {
                if (_audioPlaybackStatus == value)
                {
                    return;
                }

                _audioPlaybackStatus = value;
                RaiseAudioPlaybackUiChanged();
            }
        }

        /// <summary>Current (or frozen-on-pause) playback position in seconds.</summary>
        public uint AudioPlaybackPositionSeconds
        {
            get => _audioPlaybackPositionSeconds;
            set
            {
                if (_audioPlaybackPositionSeconds == value)
                {
                    return;
                }

                _audioPlaybackPositionSeconds = value;
                Raise(
                    nameof(AudioPlaybackPositionSeconds),
                    nameof(AudioSliderValue),
                    nameof(AudioButtonText),
                    nameof(AudioTimestampText));
            }
        }

        public string AudioButtonText
        {
            get
            {
                string kind = Model.IsVoiceMessage
                    ? (_strings != null
                        ? _strings.Get("ChatList_PreviewVoice", "Voice message")
                        : "Voice message")
                    : (_strings != null
                        ? _strings.Get("ChatDetail_Audio", "Audio")
                        : "Audio");

                uint seconds;
                if (_audioPlaybackStatus == AudioPlaybackStatus.Playing ||
                    _audioPlaybackStatus == AudioPlaybackStatus.Paused)
                {
                    seconds = _audioPlaybackPositionSeconds;
                }
                else
                {
                    seconds = Model.AudioDurationSeconds;
                }

                if (seconds == 0 &&
                    _audioPlaybackStatus != AudioPlaybackStatus.Playing &&
                    _audioPlaybackStatus != AudioPlaybackStatus.Paused)
                {
                    return kind;
                }

                return string.Format("{0}  {1}:{2:00}", kind, seconds / 60, seconds % 60);
            }
        }

        /// <summary>Download glyph for <see cref="AudioPlaybackStatus.NotDownloaded"/> / <see cref="AudioPlaybackStatus.NotAvailable"/>.</summary>
        public bool ShowAudioDownloadIcon =>
            Model.IsAudio &&
            (_audioPlaybackStatus == AudioPlaybackStatus.NotDownloaded ||
             _audioPlaybackStatus == AudioPlaybackStatus.NotAvailable);

        /// <summary>Red EBFF warning next to download when media cannot be fetched.</summary>
        public bool ShowAudioUnavailableGlyph =>
            Model.IsAudio && _audioPlaybackStatus == AudioPlaybackStatus.NotAvailable;

        public bool ShowAudioLoading =>
            Model.IsAudio && _audioPlaybackStatus == AudioPlaybackStatus.Downloading;

        public bool ShowAudioPlayButton =>
            Model.IsAudio &&
            (_audioPlaybackStatus == AudioPlaybackStatus.Ready ||
             _audioPlaybackStatus == AudioPlaybackStatus.Paused);

        public bool ShowAudioPauseButton =>
            Model.IsAudio && _audioPlaybackStatus == AudioPlaybackStatus.Playing;

        /// <summary>Ready / Playing / Paused transport row (play — label|slider — time).</summary>
        public bool ShowAudioTransportBar =>
            Model.IsAudio &&
            (_audioPlaybackStatus == AudioPlaybackStatus.Ready ||
             _audioPlaybackStatus == AudioPlaybackStatus.Playing ||
             _audioPlaybackStatus == AudioPlaybackStatus.Paused);

        /// <summary>Label ("Voice message") only while Ready — slider replaces it when Playing/Paused.</summary>
        public bool ShowAudioReadyLabel =>
            Model.IsAudio && _audioPlaybackStatus == AudioPlaybackStatus.Ready;

        public bool ShowAudioSlider =>
            Model.IsAudio &&
            (_audioPlaybackStatus == AudioPlaybackStatus.Playing ||
             _audioPlaybackStatus == AudioPlaybackStatus.Paused);

        /// <summary>
        /// "…" overflow (Save as…) while Ready and already downloaded — hidden while playing.
        /// </summary>
        public bool ShowAudioOverflowMenu =>
            Model.IsAudio &&
            HasLocalAudio &&
            _audioPlaybackStatus == AudioPlaybackStatus.Ready;

        /// <summary>Running/paused clock in the transport bar (replaces the overflow while active).</summary>
        public bool ShowAudioBarTimestamp =>
            Model.IsAudio &&
            (_audioPlaybackStatus == AudioPlaybackStatus.Playing ||
             _audioPlaybackStatus == AudioPlaybackStatus.Paused);

        public bool CanSaveAudioAs => ShowAudioOverflowMenu && _audioExport != null;

        /// <summary>Total duration in the bubble footer (next to the sound glyph).</summary>
        public bool ShowAudioFooterDuration =>
            Model.IsAudio && Model.AudioDurationSeconds > 0;

        public string AudioFooterDurationText
        {
            get
            {
                if (!ShowAudioFooterDuration)
                {
                    return string.Empty;
                }

                uint seconds = Model.AudioDurationSeconds;
                return string.Format("{0}:{1:00}", seconds / 60, seconds % 60);
            }
        }

        /// <summary>Kind label without duration (duration lives in the footer / bar clock).</summary>
        public string AudioReadyLabelText =>
            Model.IsVoiceMessage
                ? (_strings != null
                    ? _strings.Get("ChatList_PreviewVoice", "Voice message")
                    : "Voice message")
                : (_strings != null
                    ? _strings.Get("ChatDetail_Audio", "Audio")
                    : "Audio");

        /// <summary>Bar clock while Playing/Paused (position). Ready uses the footer for total.</summary>
        public string AudioTimestampText
        {
            get
            {
                uint seconds =
                    _audioPlaybackStatus == AudioPlaybackStatus.Playing ||
                    _audioPlaybackStatus == AudioPlaybackStatus.Paused
                        ? _audioPlaybackPositionSeconds
                        : Model.AudioDurationSeconds;
                return string.Format("{0}:{1:00}", seconds / 60, seconds % 60);
            }
        }

        public double AudioSliderMaximum =>
            Model.AudioDurationSeconds > 0 ? Model.AudioDurationSeconds : 1;

        public double AudioSliderValue => _audioPlaybackPositionSeconds;

        public bool NeedsAudioDownload => Model.NeedsAudioDownload;
        /// <summary>Keys allow download — includes retry after <see cref="AudioPlaybackStatus.NotAvailable"/>.</summary>
        public bool CanDownloadAudio => Model.CanDownloadAudio;
        public bool HasLocalAudio => Model.HasLocalAudio;

        /// <summary>Recompute status from persisted media when not mid-playback / error.</summary>
        public void SyncAudioPlaybackStatusFromModel()
        {
            if (!Model.IsAudio)
            {
                return;
            }

            // Keep explicit error / active transport; user retries via download.
            if (_audioPlaybackStatus == AudioPlaybackStatus.Playing ||
                _audioPlaybackStatus == AudioPlaybackStatus.Paused ||
                _audioPlaybackStatus == AudioPlaybackStatus.Downloading ||
                _audioPlaybackStatus == AudioPlaybackStatus.NotAvailable)
            {
                return;
            }

            if (Model.HasLocalAudio)
            {
                AudioPlaybackStatus = AudioPlaybackStatus.Ready;
                return;
            }

            AudioPlaybackStatus = Model.CanDownloadAudio
                ? AudioPlaybackStatus.NotDownloaded
                : AudioPlaybackStatus.NotAvailable;
        }

        /// <summary>
        /// Play/download failed — show download + error badge and clear local URI so retry re-fetches.
        /// </summary>
        public void MarkAudioUnavailable()
        {
            _audioPlaybackPositionSeconds = 0;
            try
            {
                // Drop bad/corrupt local pointer so NeedsAudioDownload becomes true again.
                if (!string.IsNullOrWhiteSpace(Model.AudioUri))
                {
                    Model.AudioUri = null;
                }
            }
            catch
            {
            }

            AudioPlaybackStatus = AudioPlaybackStatus.NotAvailable;
            Raise(
                nameof(AudioPlaybackPositionSeconds),
                nameof(AudioButtonText),
                nameof(NeedsAudioDownload),
                nameof(CanDownloadAudio),
                nameof(HasLocalAudio));
        }

        public void ResetAudioPlaybackToReady()
        {
            _audioPlaybackPositionSeconds = 0;
            AudioPlaybackStatus = Model.HasLocalAudio
                ? AudioPlaybackStatus.Ready
                : (Model.CanDownloadAudio ? AudioPlaybackStatus.NotDownloaded : AudioPlaybackStatus.NotAvailable);
            Raise(nameof(AudioPlaybackPositionSeconds), nameof(AudioButtonText));
        }

        private void RaiseAudioPlaybackUiChanged()
        {
            Raise(
                nameof(AudioPlaybackStatus),
                nameof(ShowAudioDownloadIcon),
                nameof(ShowAudioUnavailableGlyph),
                nameof(ShowAudioLoading),
                nameof(ShowAudioPlayButton),
                nameof(ShowAudioPauseButton),
                nameof(ShowAudioTransportBar),
                nameof(ShowAudioReadyLabel),
                nameof(ShowAudioSlider),
                nameof(ShowAudioOverflowMenu),
                nameof(ShowAudioBarTimestamp),
                nameof(CanSaveAudioAs),
                nameof(CanDownloadAudio),
                nameof(AudioButtonText),
                nameof(AudioReadyLabelText),
                nameof(AudioTimestampText),
                nameof(ShowAudioFooterDuration),
                nameof(AudioFooterDurationText),
                nameof(MediaBadgeDurationText),
                nameof(AudioSliderMaximum),
                nameof(AudioSliderValue));
            RaiseMediaCommandsChanged();
        }

        private void RaiseAudioModelUiChanged()
        {
            SyncAudioPlaybackStatusFromModel();
            Raise(
                nameof(NeedsAudioDownload),
                nameof(CanDownloadAudio),
                nameof(HasLocalAudio),
                nameof(AudioButtonText),
                nameof(IsAudio),
                nameof(ShowTextContent),
                nameof(ShowAudioOverflowMenu),
                nameof(CanSaveAudioAs),
                nameof(ShowAudioFooterDuration),
                nameof(AudioFooterDurationText),
                nameof(MediaBadgeDurationText));
            RaiseMediaCommandsChanged();
        }

        private void RaiseQuoteUiChanged()
        {
            Raise(
                nameof(HasQuote),
                nameof(QuotedText),
                nameof(QuotedKind),
                nameof(QuotedPreviewKind),
                nameof(QuotedStripText),
                nameof(QuotedSenderName),
                nameof(QuotedParticipantJid),
                nameof(CanOpenQuotedAuthor),
                nameof(CanNavigateToQuote),
                nameof(QuotedMessageId));
        }

        private void RaiseImageStickerUiChanged()
        {
            Raise(
                nameof(ShowImageDownloadIcon),
                nameof(CanDownloadImage),
                nameof(ShowStickerError),
                nameof(ShowStickerLoading),
                nameof(ShowStickerImage),
                nameof(IsSticker),
                nameof(HasImage),
                nameof(ImageUri),
                nameof(ThumbnailUri),
                nameof(HasMediaThumbnail),
                nameof(ShowImageDownloadPlaceholderGlyph),
                nameof(InfoPreviewUri),
                nameof(HasInfoPreviewUri),
                nameof(NeedsImageDownload),
                nameof(IsImage),
                nameof(CanOpenImageViewer),
                nameof(ShowTextContent));
            RaiseMediaCommandsChanged();
        }

        private void RaiseVideoUiChanged()
        {
            Raise(
                nameof(IsVideo),
                nameof(IsGif),
                nameof(IsAnimated),
                nameof(IsImage),
                nameof(NeedsVideoDownload),
                nameof(CanDownloadVideo),
                nameof(HasLocalVideo),
                nameof(HasVideoPoster),
                nameof(VideoUri),
                nameof(VideoPosterUri),
                nameof(InfoPreviewUri),
                nameof(HasInfoPreviewUri),
                nameof(ShowVideoDownloadIcon),
                nameof(ShowVideoPlayOverlay),
                nameof(ShowGifSurface),
                nameof(ShowGifPlayButton),
                nameof(ResumeGifWhenVisible),
                nameof(ShowVideoFooterDuration),
                nameof(VideoFooterDurationText),
                nameof(MediaBadgeDurationText),
                nameof(ShowTextContent));
            RaiseMediaCommandsChanged();
            (PlayGifCommand as RelayCommand)?.RaiseCanExecuteChanged();
            if (Model.IsGif)
            {
                EnsureGifPlaybackRegistration();
            }
            else
            {
                UnregisterGifPlayback();
            }

            ScheduleGifAutoDownloadIfNeeded();
        }

        private void RaiseDocumentUiChanged()
        {
            Raise(
                nameof(IsDocument),
                nameof(DocumentFileName),
                nameof(DocumentDisplayName),
                nameof(DocumentUri),
                nameof(DocumentFileLengthBytes),
                nameof(HasLocalDocument),
                nameof(HasDocumentFileSize),
                nameof(ShowDocumentFileSize),
                nameof(NeedsDocumentDownload),
                nameof(CanDownloadDocument),
                nameof(ShowDocumentDownloadIcon),
                nameof(ShowDocumentUnavailableGlyph),
                nameof(ShowDocumentReadyChrome),
                nameof(ShowTextContent));
            RaiseMediaCommandsChanged();
        }

        private void RaiseContactUiChanged()
        {
            EnsureContactCardPhoto();
            Raise(
                nameof(IsContact),
                nameof(ContactCardDisplayName),
                nameof(ContactCardPhotoUri),
                nameof(HasContactCardPhoto),
                nameof(CanMessageContact),
                nameof(CanAddSharedContact),
                nameof(ShowMessageContactAddButton),
                nameof(ShowTextContent));
            (MessageContactChatCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MessageContactAddCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public string ImageUri => Model.ImageUri;

        /// <summary>Local picture for a minimized reply chrome. Thumbnail when the full file is not here.</summary>
        public string PreviewImageUri
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Model.ImageUri))
                {
                    return Model.ImageUri;
                }

                if (!string.IsNullOrWhiteSpace(Model.VideoPosterUri))
                {
                    return Model.VideoPosterUri;
                }

                return string.IsNullOrWhiteSpace(Model.ThumbnailUri) ? null : Model.ThumbnailUri;
            }
        }
        public bool HasImage => Model.HasImage;
        public string ThumbnailUri => Model.ThumbnailUri;
        public bool HasMediaThumbnail => Model.HasMediaThumbnail;
        /// <summary>Generic download glyph when no protocol thumb is on disk yet.</summary>
        public bool ShowImageDownloadPlaceholderGlyph => NeedsImageDownload && !HasMediaThumbnail;
        /// <summary>Protocol image bubble (Kind.Image) — footer photo glyph.</summary>
        public bool IsImage => Model.IsImage;
        /// <summary>Photo bubble with a local file — opens the image viewer on tap.</summary>
        public bool CanOpenImageViewer => IsImage && HasImage;
        /// <summary>Sticker with a local file — shown inline, never opens the image viewer.</summary>
        public bool ShowStickerImage => IsSticker && HasImage;
        public bool NeedsImageDownload => Model.NeedsImageDownload;
        public bool CanDownloadImage => Model.CanDownloadImage;
        public bool IsSticker => Model.IsSticker;
        public bool ShowStickerError => Model.ShowStickerError;
        public bool ShowStickerLoading => Model.ShowStickerLoading;
        public string Caption => Model.Caption;

        private string CopyableText
        {
            get
            {
                if (ShowTextContent && !string.IsNullOrWhiteSpace(Content))
                {
                    return Content;
                }

                if (!string.IsNullOrWhiteSpace(Caption))
                {
                    return Caption;
                }

                return null;
            }
        }

        private void CopyText()
        {
            string text = CopyableText;
            if (string.IsNullOrWhiteSpace(text) || _copy == null)
            {
                return;
            }

            _copy.CopyText(text);
        }
        public bool HasCaption => Model.HasCaption;
        public bool ShowTextContent => Model.ShowTextContent;

        public bool IsRevoked => Model.IsRevoked;
        public bool HasQuote => Model.HasQuote;
        public string QuotedText => Model.QuotedText;
        public ChatPreviewKind QuotedKind => Model.QuotedKind;
        public string QuotedSenderName => Model.QuotedSenderName;
        public string QuotedParticipantJid => Model.QuotedParticipantJid;
        /// <summary>Group quote author name is tappable (JID and/or visible @name).</summary>
        public bool CanOpenQuotedAuthor => Model.ShowQuotedAuthorLink;
        /// <summary>Quote strip is actionable when the protocol stanza id is known.</summary>
        public bool CanNavigateToQuote => !string.IsNullOrWhiteSpace(Model.QuotedMessageId);
        public string QuotedMessageId => Model.QuotedMessageId;

        /// <summary>Kind for the quote strip (falls back to legacy [Image]/[Document] tags).</summary>
        public ChatPreviewKind QuotedPreviewKind
        {
            get
            {
                if (_quotedPreviewKindCached.HasValue)
                {
                    return _quotedPreviewKindCached.Value;
                }

                ChatPreviewKind kind = Model.QuotedKind != ChatPreviewKind.Text
                    ? Model.QuotedKind
                    : ChatPreviewNormalizer.InferKindFromLegacyMediaTags(Model.QuotedText);
                _quotedPreviewKindCached = kind;
                return kind;
            }
        }

        /// <summary>Caption / body after media tags are stripped for the quote strip.</summary>
        public string QuotedStripText
        {
            get
            {
                if (_quotedStripTextCached != null)
                {
                    return _quotedStripTextCached;
                }

                ChatPreviewKind kind = QuotedPreviewKind;
                ChatPreviewKind? hint = kind == ChatPreviewKind.Text
                    ? null
                    : (ChatPreviewKind?)kind;
                ChatPreviewNormalizer.Normalize(Model.QuotedText, hint, out _, out string text);
                _quotedStripTextCached = text ?? string.Empty;
                return _quotedStripTextCached;
            }
        }

        /// <summary>Transient UI flash after scrolling to this bubble from a reply tap.</summary>
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted == value) return;
                _isHighlighted = value;
                Raise(nameof(IsHighlighted));
            }
        }

        public System.Collections.Generic.IList<string> MentionedJids => Model.MentionedJids;

        private System.Collections.Generic.IReadOnlyDictionary<string, string> _mentionLookup;
        /// <summary>Digit → name map from the chat roster for @mention resolution.</summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, string> MentionLookup => _mentionLookup;

        /// <summary>Bumped when the lookup is replaced so the bubble re-parses mentions.</summary>
        public int MentionRefreshKey { get; private set; }

        public void AttachMentionLookup(System.Collections.Generic.IReadOnlyDictionary<string, string> lookup)
        {
            _mentionLookup = lookup;
        }

        public void RefreshMentions(System.Collections.Generic.IReadOnlyDictionary<string, string> lookup)
        {
            bool changed = !object.ReferenceEquals(_mentionLookup, lookup);
            _mentionLookup = lookup;
            if (changed)
            {
                Raise(nameof(MentionLookup));
                MentionRefreshKey++;
                Raise(nameof(MentionRefreshKey));
            }
        }

        private bool _isImageDownloading;
        public bool IsImageDownloading
        {
            get => _isImageDownloading;
            set
            {
                if (_isImageDownloading == value) return;
                _isImageDownloading = value;
                Raise(nameof(IsImageDownloading), nameof(ShowImageDownloadIcon));
            }
        }

        /// <summary>Download glyph visible when pending and not mid-download.</summary>
        public bool ShowImageDownloadIcon => NeedsImageDownload && !IsImageDownloading;

        private bool _isVideoDownloading;
        public bool IsVideoDownloading
        {
            get => _isVideoDownloading;
            set
            {
                if (_isVideoDownloading == value) return;
                _isVideoDownloading = value;
                Raise(nameof(IsVideoDownloading), nameof(ShowVideoDownloadIcon));
            }
        }

        public bool IsVideo => Model.IsVideo;
        public bool IsGif => Model.IsGif;
        /// <summary>WhatsApp GIF (<c>GifPlayback</c>) — same as <see cref="IsGif"/>.</summary>
        public bool IsAnimated => Model.IsAnimated;
        public string VideoUri => Model.VideoUri;
        public string VideoPosterUri => Model.VideoPosterUri;
        public bool HasLocalVideo => Model.HasLocalVideo;
        public bool HasVideoPoster => Model.HasVideoPoster;
        public bool NeedsVideoDownload => Model.NeedsVideoDownload;
        public bool CanDownloadVideo => Model.CanDownloadVideo;

        /// <summary>
        /// Download affordance for video/GIF. Hidden on High while a GIF is expected to
        /// auto-fetch (retry icon only after that silent attempt fails).
        /// </summary>
        public bool ShowVideoDownloadIcon =>
            NeedsVideoDownload &&
            !IsVideoDownloading &&
            !(IsGif && _deviceCategory == DeviceCategory.High && !_gifAutoDownloadFailed);

        /// <summary>Completes when the High-tier GIF auto-download finishes (or was a no-op).</summary>
        public Task GifAutoDownloadCompletion => _gifAutoDownloadTask ?? Task.CompletedTask;
        /// <summary>Poster + play balloon once the local video file exists (not GIF).</summary>
        public bool ShowVideoPlayOverlay => Model.IsVideo && Model.HasLocalVideo;

        /// <summary>GIF bubble surface (poster + optional AnimationView) once the file exists.</summary>
        public bool ShowGifSurface => Model.IsGif && Model.HasLocalVideo;

        /// <summary>Segoe MDL2 GIF glyph button — visible while the animation is paused/static.</summary>
        public bool ShowGifPlayButton => ShowGifSurface && !IsGifPlaying;

        /// <summary>
        /// Session flag: user already tapped play. Used with device tier for viewport resume.
        /// </summary>
        public bool HasStartedGifPlayback => _hasStartedGifPlayback;

        /// <summary>True while the in-bubble GIF player is running.</summary>
        public bool IsGifPlaying
        {
            get => _isGifPlaying;
            set
            {
                if (_isGifPlaying == value)
                {
                    return;
                }

                _isGifPlaying = value;
                Raise(
                    nameof(IsGifPlaying),
                    nameof(ShowGifPlayButton));
                (PlayGifCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// Low+ resume GIF when scrolling back into view after a prior play.
        /// Unsupported never auto-resumes.
        /// </summary>
        public bool ResumeGifWhenVisible =>
            _deviceCategory != DeviceCategory.Unsupported && _hasStartedGifPlayback;

        /// <summary>Duration next to the footer video glyph — only after download (not GIF).</summary>
        public bool ShowVideoFooterDuration =>
            Model.IsVideo &&
            Model.HasLocalVideo &&
            Model.VideoDurationSeconds > 0;

        /// <summary>Starts in-bubble GIF playback; does not open the fullscreen viewer.</summary>
        public void StartGifPlayback()
        {
            if (!ShowGifSurface)
            {
                return;
            }

            EnsureGifPlaybackRegistration();
            _gifPlayback?.NotifyPlaying(this);
            _hasStartedGifPlayback = true;
            IsGifPlaying = true;
            Raise(nameof(HasStartedGifPlayback), nameof(ResumeGifWhenVisible));
            (PlayGifCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>Pauses in-bubble GIF (viewport leave). Keeps <see cref="HasStartedGifPlayback"/>.</summary>
        public void PauseGifPlayback()
        {
            IsGifPlaying = false;
        }

        /// <summary>Exclusive GIF slot on Unsupported — other bubbles claim play.</summary>
        public void PauseFromCoordinator()
        {
            PauseGifPlayback();
        }

        private void EnsureGifPlaybackRegistration()
        {
            if (_gifRegistered || _gifPlayback == null || !Model.IsGif)
            {
                return;
            }

            _gifPlayback.Register(this);
            _gifRegistered = true;
        }

        private void UnregisterGifPlayback()
        {
            if (!_gifRegistered || _gifPlayback == null)
            {
                return;
            }

            _gifPlayback.Unregister(this);
            _gifRegistered = false;
        }

        public string VideoFooterDurationText
        {
            get
            {
                if (!ShowVideoFooterDuration)
                {
                    return string.Empty;
                }

                uint seconds = Model.VideoDurationSeconds;
                return string.Format("{0}:{1:00}", seconds / 60, seconds % 60);
            }
        }

        /// <summary>Duration badge for the chat-info media grid (available before download).</summary>
        public string MediaBadgeDurationText
        {
            get
            {
                uint seconds = Model.IsVideo
                    ? Model.VideoDurationSeconds
                    : Model.AudioDurationSeconds;
                if (seconds == 0)
                {
                    return string.Empty;
                }

                return string.Format("{0}:{1:00}", seconds / 60, seconds % 60);
            }
        }

        private bool _isDocumentDownloading;
        private bool _isDocumentDownloadFailed;

        public bool IsDocumentDownloading
        {
            get => _isDocumentDownloading;
            set
            {
                if (_isDocumentDownloading == value) return;
                _isDocumentDownloading = value;
                if (value)
                {
                    _isDocumentDownloadFailed = false;
                }

                Raise(
                    nameof(IsDocumentDownloading),
                    nameof(ShowDocumentDownloadIcon),
                    nameof(ShowDocumentUnavailableGlyph),
                    nameof(ShowDocumentReadyChrome),
                    nameof(ShowDocumentFileSize));
                RaiseMediaCommandsChanged();
            }
        }

        public bool IsDocument => Model.IsDocument;

        public bool IsContact => Model.IsContact;

        public string ContactCardDisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Model.ContactCardDisplayName))
                {
                    return Model.ContactCardDisplayName.Trim();
                }

                ChatPreviewNormalizer.Normalize(
                    Model.Content,
                    ChatPreviewKind.Contact,
                    out _,
                    out string fromContent);
                return !string.IsNullOrWhiteSpace(fromContent)
                    ? fromContent
                    : (_strings?.Get("ChatDetail_ContactFallbackName", "Contact") ?? "Contact");
            }
        }

        public string ContactCardPhotoUri => Model.ContactCardPhotoUri;

        public bool HasContactCardPhoto => Model.HasContactCardPhoto;

        /// <summary>True when the shared card has a JID we can open a chat with.</summary>
        public bool CanMessageContact =>
            IsContact && !string.IsNullOrWhiteSpace(Model.ContactCardJid);

        /// <summary>
        /// True when the shared contact is not already in the device agenda (Conversar + Adicionar).
        /// When false, only Conversar is shown.
        /// </summary>
        public bool CanAddSharedContact =>
            IsContact &&
            _contacts != null &&
            _contacts.CanAddToAddressBook(Model.ContactCardJid, Model.ContactCardPhone);

        public bool ShowMessageContactAddButton => CanAddSharedContact;

        private void EnsureContactCardPhoto()
        {
            if (!IsContact || HasContactCardPhoto || _people == null)
            {
                return;
            }

            string jid = Model.ContactCardJid;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            Person person = _people.TryGetCached(jid);
            if (person != null && !string.IsNullOrWhiteSpace(person.AvatarUrl))
            {
                Model.ContactCardPhotoUri = person.AvatarUrl;
            }
        }

        private void MessageContactChat()
        {
            string jid = Model.ContactCardJid;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            _chats?.StartNewChat(jid);
            if (_openChat != null)
            {
                _openChat(jid);
            }
        }

        private async Task MessageContactAddAsync()
        {
            if (!CanAddSharedContact)
            {
                return;
            }

            try
            {
                await _contacts.ShowAddToAddressBookAsync(
                    ContactCardDisplayName,
                    Model.ContactCardPhone,
                    Model.ContactCardPhotoUri,
                    Model.ContactCardJid);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatMessageViewModel] Add shared contact failed: " + ex.Message);
            }

            Raise(
                nameof(CanAddSharedContact),
                nameof(ShowMessageContactAddButton));
            (MessageContactAddCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public string DocumentFileName => Model.DocumentFileName;
        public string DocumentDisplayName
        {
            get
            {
                if (_documentDisplayNameCached != null)
                {
                    return _documentDisplayNameCached;
                }

                string resolved = ChatPreviewNormalizer.ResolveDocumentDisplayName(
                    Model.DocumentFileName,
                    Model.Content);
                _documentDisplayNameCached = !string.IsNullOrWhiteSpace(resolved)
                    ? resolved
                    : (_strings?.Get("ChatDetail_DocumentFallbackName", "Document") ?? "Document");
                return _documentDisplayNameCached;
            }
        }
        public string DocumentUri => Model.DocumentUri;
        public long DocumentFileLengthBytes => Model.DocumentFileLengthBytes;
        public bool HasDocumentFileSize => Model.HasDocumentFileSize;
        public bool HasLocalDocument => Model.HasLocalDocument;
        public bool NeedsDocumentDownload => Model.NeedsDocumentDownload;
        /// <summary>Always allow the download tap; missing keys surface as failed + red badge.</summary>
        public bool CanDownloadDocument => NeedsDocumentDownload;
        public bool ShowDocumentDownloadIcon => NeedsDocumentDownload && !IsDocumentDownloading;
        /// <summary>Red EBFF warning on top of download when the last attempt failed (retry still allowed).</summary>
        public bool ShowDocumentUnavailableGlyph =>
            NeedsDocumentDownload && _isDocumentDownloadFailed && !IsDocumentDownloading;
        /// <summary>Ready chrome (context affordance) when local file exists and not downloading.</summary>
        public bool ShowDocumentReadyChrome => HasLocalDocument && !IsDocumentDownloading;
        /// <summary>File size in the bubble footer after the document is available locally.</summary>
        public bool ShowDocumentFileSize =>
            HasLocalDocument && HasDocumentFileSize && !IsDocumentDownloading;

        /// <summary>Download/open failed — overlay red badge; downloading cleared by caller finally.</summary>
        public void MarkDocumentUnavailable()
        {
            _isDocumentDownloadFailed = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(Model.DocumentUri))
                {
                    Model.DocumentUri = null;
                }
            }
            catch
            {
            }

            Raise(
                nameof(ShowDocumentUnavailableGlyph),
                nameof(HasLocalDocument),
                nameof(NeedsDocumentDownload),
                nameof(CanDownloadDocument),
                nameof(ShowDocumentDownloadIcon),
                nameof(ShowDocumentReadyChrome),
                nameof(ShowDocumentFileSize));
            RaiseMediaCommandsChanged();
        }

        public string RemoteJid
        {
            get => Model.RemoteJid;
            set => Model.RemoteJid = value;
        }

        public string ParticipantJid
        {
            get => Model.ParticipantJid;
            set => Model.ParticipantJid = value;
        }

        public bool IsPinned
        {
            get => Model.IsPinned;
            set => Model.IsPinned = value;
        }

        public DateTime? PinnedAtUtc
        {
            get => Model.PinnedAtUtc;
            set => Model.PinnedAtUtc = value;
        }

        public DateTime? PinExpiresAtUtc
        {
            get => Model.PinExpiresAtUtc;
            set => Model.PinExpiresAtUtc = value;
        }

        public bool HasReactions => Model.HasReactions;
        public int TotalReactions => Model.TotalReactions;
        public string ReactionsDisplayText => Model.ReactionsDisplayText;

        public bool IsRunStart
        {
            get => Model.IsRunStart;
            set => Model.IsRunStart = value;
        }

        public bool IsRunEnd
        {
            get => Model.IsRunEnd;
            set => Model.IsRunEnd = value;
        }

        public bool ShowTail => Model.ShowTail;

        /// <summary>True when <see cref="RemoteJid"/> is a group (@g.us).</summary>
        public bool IsGroupChat
        {
            get
            {
                string jid = Model?.RemoteJid;
                return !string.IsNullOrWhiteSpace(jid) &&
                       jid.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public bool IsFirstOfDay
        {
            get => Model.IsFirstOfDay;
            set => Model.IsFirstOfDay = value;
        }

        public string DateSeparatorText
        {
            get => Model.DateSeparatorText;
            set => Model.DateSeparatorText = value;
        }

        public bool ShowGroupSenderName
        {
            get => Model.ShowGroupSenderName;
            set => Model.ShowGroupSenderName = value;
        }

        public bool ShowQuotedAuthorLink
        {
            get => Model.ShowQuotedAuthorLink;
            set => Model.ShowQuotedAuthorLink = value;
        }

        public bool IsForwarded => Model.IsForwarded;

        public bool ShowForwardedLabel => Model.IsForwarded;

        public string ForwardedLabelText =>
            _strings != null
                ? _strings.Get("ChatDetail_Forwarded", "Forwarded")
                : "Forwarded";

        /// <summary>Group author photo URI for the bubble-side avatar.</summary>
        public string ContactUri
        {
            get => Model.ContactUri;
            set => Model.ContactUri = value;
        }

        /// <summary>True when the author avatar should be drawn beside this bubble.</summary>
        public bool ShowContact
        {
            get => Model.ShowContact;
            set => Model.ShowContact = value;
        }

        /// <summary>Reserves avatar gutter width for all incoming group bubbles.</summary>
        public bool ShowContactSlot
        {
            get => Model.ShowContactSlot;
            set => Model.ShowContactSlot = value;
        }

        public string FormattedTime => Model.FormattedTime;

        public string FormattedDate => Model.FormattedDate;

        /// <summary>Local image for the chat-info media grid (downloaded file, then jpegThumbnail cache).</summary>
        public string InfoPreviewUri
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Model.ImageUri))
                {
                    return Model.ImageUri;
                }

                if (!string.IsNullOrWhiteSpace(Model.ThumbnailUri))
                {
                    return Model.ThumbnailUri;
                }

                if (Model.IsVideo && Model.HasVideoPoster)
                {
                    return Model.VideoPosterUri;
                }

                return null;
            }
        }

        public bool HasInfoPreviewUri => !string.IsNullOrWhiteSpace(InfoPreviewUri);

        private void RaiseExpandPropsChanged()
        {
            Raise(
                nameof(IsExpanded),
                nameof(CanExpand),
                nameof(ContentMaxLines),
                nameof(ExpandLinkText));
            (ToggleExpandCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>Local equivalent of <c>Observable.RaiseProperties</c> (this VM is not an Observable).</summary>
        private void Raise(params string[] propertyNames)
        {
            PostToUi(() => RaiseCore(propertyNames));
        }

        private void RaiseCore(params string[] propertyNames)
        {
            var handler = PropertyChanged;
            if (handler == null || propertyNames == null)
            {
                return;
            }

            for (int i = 0; i < propertyNames.Length; i++)
            {
                handler(this, new PropertyChangedEventArgs(propertyNames[i]));
            }
        }

        /// <summary>
        /// Heuristic for SDK &lt; 16299 (no <c>IsTextTrimmed</c>): hard newlines or soft-wrap estimate.
        /// </summary>
        private static bool NeedsCollapse(string text, int maxLines)
        {
            if (string.IsNullOrEmpty(text) || maxLines <= 0)
            {
                return false;
            }

            int hardLines = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' || text[i] == '\r')
                {
                    if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    hardLines++;
                    if (hardLines > maxLines)
                    {
                        return true;
                    }
                }
            }

            // ~40 chars per wrapped line in a typical bubble width.
            return text.Length > maxLines * 40;
        }
    }
}
