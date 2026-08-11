using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Active conversation: composer, send text/media, mic session + overlay, load-more / history,
    /// pin / audio prepare, and presence watch (Storyboards stay in the view).
    /// Timeline items are <see cref="ChatMessageViewModel"/> via <see cref="IChatMessageVmFactory"/>.
    /// </summary>
    public class ChatDetailViewModel : Observable
    {
        // ── DI ────────────────────────────────────────────────────────────────

        /// <summary>Load / live updates / presence for the active JID.</summary>
        private readonly IWhatsAppService _whatsAppService;

        /// <summary>Send text / image / audio / pin via the message facade.</summary>
        private readonly IMessageService _messageService;

        /// <summary>Unitary microphone capture (returns session handles).</summary>
        private readonly IAudioRecordingService _audioRecording;

        /// <summary>File pickers — chat attach uses PickChatAttachmentAsync.</summary>
        private readonly IFilePicker _filePicker;

        /// <summary>Confirm/preview/error dialogs (no ContentDialog in Core).</summary>
        private readonly IDialogService _dialogs;

        /// <summary>UI-thread marshaling for live updates / elapsed ticks.</summary>
        private readonly IDispatcher _dispatcher;

        /// <summary>On-demand bubble VMs for the timeline.</summary>
        private readonly IChatMessageVmFactory _messageFactory;

        /// <summary>Localized presence / header subtitle copy.</summary>
        private readonly IStringResources _strings;

        // ── State ─────────────────────────────────────────────────────────────

        private ChatItem _activeChat;
        private string _messageText;
        private bool _isSending;
        private bool _isRecording;
        private string _recordingElapsedText = "0:00";
        private bool _hasActiveChat;
        private bool _isLoadingMessages;
        private bool _isLoadingMore;
        private string _recordingChatJid;

        private IAudioRecordingSession _recordingSession;
        private CancellationTokenSource _elapsedCts;
        private CancellationTokenSource _loadCts;
        private CancellationTokenSource _presenceCts;
        private bool _attached;
        private bool _presenceHandlerAttached;
        private bool _presenceReceived;
        private string _pendingPresenceText;
        private DateTime _presenceOpenedUtc;

        public ChatDetailViewModel(
            IWhatsAppService whatsAppService,
            IMessageService messageService,
            IAudioRecordingService audioRecording,
            IFilePicker filePicker,
            IDialogService dialogs,
            IDispatcher dispatcher,
            IChatMessageVmFactory messageFactory,
            IStringResources strings)
        {
            _whatsAppService = whatsAppService;
            _messageService = messageService;
            _audioRecording = audioRecording;
            _filePicker = filePicker;
            _dialogs = dialogs;
            _dispatcher = dispatcher;
            _messageFactory = messageFactory ?? throw new ArgumentNullException(nameof(messageFactory));
            _strings = strings;

            Messages = new ObservableCollection<ChatMessageViewModel>();

            SendMessageCommand = new RelayCommand(
                async () => await SendMessageAsync(),
                () => CanCompose && !string.IsNullOrWhiteSpace(MessageText));

            AttachMediaCommand = new RelayCommand(
                async () => await AttachMediaAsync(),
                () => CanCompose);

            StartRecordingCommand = new RelayCommand(
                async () => await StartRecordingAsync(),
                () => CanCompose);

            CancelRecordingCommand = new RelayCommand(
                async () => await CancelRecordingCoreAsync(),
                () => _isRecording);

            SendRecordingCommand = new RelayCommand(
                async () => await StopAndSendRecordingAsync(),
                () => _isRecording && !_isSending);

            BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public Task InitializeAsync()
        {
            Attach();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Leave the detail surface: stop elapsed UI, cancel mic, stop presence watch.
        /// Idempotent — call from view Unloaded / navigate-away.
        /// </summary>
        public async Task UninitializeAsync()
        {
            StopPresenceWatch();
            await CancelRecordingCoreAsync();
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            // Live merge into Messages is owned by ChatDetailView.SyncMessagesFromServiceAsync
            // until that path is fully moved here (avoids double-append on the same collection).
        }

        /// <summary>Wrap a domain message for the timeline (view load/sync paths).</summary>
        public ChatMessageViewModel CreateMessageVm(ChatMessage message)
        {
            return _messageFactory.Create(message);
        }

        // ── Events ────────────────────────────────────────────────────────────

        public event EventHandler BackRequested;
        public event EventHandler MessageSent;

        /// <summary>Raised after pin/unpin succeeds (view refreshes pinned banner).</summary>
        public event EventHandler MessagePinnedChanged;

        /// <summary>
        /// Presence watch finished: non-null text → animate status; null → fallback-only sequence.
        /// Storyboards remain in the view.
        /// </summary>
        public event EventHandler<string> PresenceAnimationRequested;

        // ── Bindable state ────────────────────────────────────────────────────

        public ObservableCollection<ChatMessageViewModel> Messages { get; }

        public ChatItem ActiveChat
        {
            get => _activeChat;
            private set
            {
                Set(ref _activeChat, value);
                HasActiveChat = value != null;
                RaiseComposerCommandsChanged();
            }
        }

        public string MessageText
        {
            get => _messageText;
            set
            {
                Set(ref _messageText, value);
                (SendMessageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool IsSending
        {
            get => _isSending;
            private set
            {
                Set(ref _isSending, value);
                RaiseComposerCommandsChanged();
            }
        }

        public bool IsRecording
        {
            get => _isRecording;
            private set
            {
                Set(ref _isRecording, value);
                RaiseComposerCommandsChanged();
            }
        }

        public string RecordingElapsedText
        {
            get => _recordingElapsedText;
            private set => Set(ref _recordingElapsedText, value);
        }

        public bool HasActiveChat
        {
            get => _hasActiveChat;
            private set => Set(ref _hasActiveChat, value);
        }

        public bool IsLoadingMessages
        {
            get => _isLoadingMessages;
            private set => Set(ref _isLoadingMessages, value);
        }

        public bool IsLoadingMore
        {
            get => _isLoadingMore;
            set => Set(ref _isLoadingMore, value);
        }

        private bool CanCompose => ActiveChat != null && !_isSending && !_isRecording;

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand SendMessageCommand { get; }
        public ICommand AttachMediaCommand { get; }
        public ICommand StartRecordingCommand { get; }
        public ICommand CancelRecordingCommand { get; }
        public ICommand SendRecordingCommand { get; }
        public ICommand BackCommand { get; }

        // ── Actions ───────────────────────────────────────────────────────────

        public void SyncActiveChat(ChatItem chat)
        {
            ActiveChat = chat;
            if (chat == null)
            {
                MessageText = string.Empty;
                StopPresenceWatch();
                _ = CancelRecordingCoreAsync();
            }
        }

        public async Task SetActiveChatAsync(ChatItem chat)
        {
            if (chat == null && _activeChat != null)
                return;

            StopPresenceWatch();
            await CancelRecordingCoreAsync().ConfigureAwait(false);

            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            if (chat != null)
            {
                string canonical = _whatsAppService.GetCanonicalJid(chat.JID);
                if (!string.IsNullOrWhiteSpace(canonical) &&
                    !string.Equals(canonical, chat.JID, StringComparison.OrdinalIgnoreCase))
                {
                    var canonicalChat = _whatsAppService.Chats.FirstOrDefault(c =>
                        string.Equals(_whatsAppService.GetCanonicalJid(c.JID), canonical, StringComparison.OrdinalIgnoreCase));
                    if (canonicalChat != null)
                        chat = canonicalChat;
                    else
                        chat.JID = canonical;
                }
            }

            ActiveChat = chat;

            if (chat == null)
            {
                Messages.Clear();
                return;
            }

            Messages.Clear();
            IsLoadingMessages = true;
            try
            {
                var loaded = await _whatsAppService.LoadMessagesForChatAsync(chat.JID);
                if (token.IsCancellationRequested) return;

                foreach (var msg in loaded ?? new List<ChatMessage>())
                {
                    if (msg != null)
                        Messages.Add(_messageFactory.Create(msg));
                }

                AppendLiveMessages();
            }
            finally
            {
                if (!token.IsCancellationRequested)
                    IsLoadingMessages = false;
            }
        }

        public async Task<bool> LoadMoreMessagesAsync()
        {
            if (_activeChat == null) return false;

            IsLoadingMore = true;
            try
            {
                var older = await _whatsAppService.LoadMoreMessagesAsync(_activeChat.JID);
                if (older == null || older.Count == 0)
                    return await _whatsAppService.EnsureHistoryOnDemandAsync(_activeChat.JID, 80);

                for (int i = 0; i < older.Count; i++)
                {
                    if (older[i] != null)
                        Messages.Insert(i, _messageFactory.Create(older[i]));
                }
                return true;
            }
            finally
            {
                IsLoadingMore = false;
            }
        }

        public bool IsHistoryOnDemandPending()
        {
            return _activeChat != null && _whatsAppService.IsHistoryOnDemandPending(_activeChat.JID);
        }

        /// <summary>Pin or unpin a message in the active chat (flyout actions).</summary>
        public async Task SetMessagePinnedAsync(ChatMessage message, bool pin, uint durationSeconds = 604800)
        {
            if (_activeChat == null || message == null || string.IsNullOrWhiteSpace(message.Id))
            {
                return;
            }

            try
            {
                await _messageService.SetMessagePinnedAsync(_activeChat.JID, message, pin, durationSeconds);
                MessagePinnedChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Pin/unpin failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Downloads/decrypts audio for playback. Returns local URI or null (dialog already shown).
        /// MediaElement play/pause stays in the view.
        /// </summary>
        public async Task<string> EnsureAudioReadyAsync(ChatMessage message)
        {
            if (message == null || !message.IsAudio)
            {
                return null;
            }

            try
            {
                string uri = await _messageService.EnsureAudioAvailableAsync(message);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Audio unavailable.");
                }

                return uri;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Audio prepare failed: " + ex.Message);
                try
                {
                    await _dialogs.ShowMessageAsync(
                        _strings.Get("Toast_AppName", "Unison"),
                        _strings.Get("ChatDetail_AudioPlayFailed", "Could not play this audio."),
                        _strings.Get("Common_OK", "OK"));
                }
                catch
                {
                }

                return null;
            }
        }

        /// <summary>
        /// Downloads/decrypts an image for the bubble. Returns local URI or null (dialog already shown).
        /// </summary>
        public async Task<string> EnsureImageReadyAsync(ChatMessage message)
        {
            if (message == null || !message.IsImage)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(message.ImageUri))
            {
                return message.ImageUri;
            }

            try
            {
                string uri = await _messageService.EnsureImageAvailableAsync(message);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Image unavailable.");
                }

                return uri;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Image prepare failed: " + ex.Message);
                try
                {
                    await _dialogs.ShowMessageAsync(
                        _strings.Get("Toast_AppName", "Unison"),
                        _strings.Get("ChatDetail_ImageDownloadFailed", "Could not download this image."),
                        _strings.Get("Common_OK", "OK"));
                }
                catch
                {
                }

                return null;
            }
        }

        /// <summary>
        /// Subscribe + wait for presence (or group hint). Raises <see cref="PresenceAnimationRequested"/>.
        /// View skips calling this on Windows Mobile (cosmetic cost).
        /// </summary>
        public void StartPresenceWatch(string jid)
        {
            StopPresenceWatch();
            if (string.IsNullOrEmpty(jid))
            {
                return;
            }

            _presenceCts = new CancellationTokenSource();
            _presenceOpenedUtc = DateTime.UtcNow;
            _presenceReceived = false;
            _pendingPresenceText = null;

            EnsurePresenceHandlerAttached();

            if (jid.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _presenceReceived = true;
                _pendingPresenceText = _strings.Get(
                    "ChatDetail_SelectForGroupInfo",
                    "select here for group info");
                _ = RunPresenceTimerAsync(_presenceCts.Token, null);
                return;
            }

            _ = RunPresenceTimerAsync(_presenceCts.Token, jid);
        }

        /// <summary>Cancel presence timer and detach handler.</summary>
        public void StopPresenceWatch()
        {
            try
            {
                _presenceCts?.Cancel();
            }
            catch
            {
            }

            _presenceCts?.Dispose();
            _presenceCts = null;
            _presenceReceived = false;
            _pendingPresenceText = null;
            DetachPresenceHandler();
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void RaiseComposerCommandsChanged()
        {
            (SendMessageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AttachMediaCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SendRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void EnsurePresenceHandlerAttached()
        {
            if (_presenceHandlerAttached)
            {
                return;
            }

            _whatsAppService.OnPresenceUpdate += WhatsApp_OnPresenceUpdate;
            _presenceHandlerAttached = true;
        }

        private void DetachPresenceHandler()
        {
            if (!_presenceHandlerAttached)
            {
                return;
            }

            _whatsAppService.OnPresenceUpdate -= WhatsApp_OnPresenceUpdate;
            _presenceHandlerAttached = false;
        }

        private void WhatsApp_OnPresenceUpdate(object sender, PresenceUpdateEventArgs e)
        {
            if (_presenceCts == null || _presenceCts.IsCancellationRequested)
            {
                return;
            }

            _presenceReceived = true;
            _pendingPresenceText = FormatPresenceText(e?.Presence, e?.LastSeen);
        }

        private async Task RunPresenceTimerAsync(CancellationToken ct, string subscribeJid)
        {
            try
            {
                bool subscribed = false;

                for (int i = 0; i < 30; i++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_presenceReceived)
                    {
                        break;
                    }

                    if (!subscribed && !string.IsNullOrEmpty(subscribeJid) && _whatsAppService.IsConnected)
                    {
                        await _whatsAppService.PresenceSubscribeAsync(subscribeJid);
                        subscribed = true;
                    }

                    await Task.Delay(100, ct);
                }

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                string text = null;
                if (_presenceReceived && !string.IsNullOrEmpty(_pendingPresenceText))
                {
                    var elapsed = (DateTime.UtcNow - _presenceOpenedUtc).TotalMilliseconds;
                    if (elapsed < 3000)
                    {
                        await Task.Delay((int)(3000 - elapsed), ct);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    text = _pendingPresenceText;
                }

                await _dispatcher.RunAsync(() =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    PresenceAnimationRequested?.Invoke(this, text);
                });
            }
            catch (TaskCanceledException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Presence timer: " + ex.Message);
            }
        }

        private string FormatPresenceText(string presence, long? lastSeenEpoch)
        {
            if (presence == "available" || presence == "composing")
            {
                return presence == "composing"
                    ? _strings.Get("ChatDetail_PresenceTyping", "typing...")
                    : _strings.Get("ChatDetail_PresenceOnline", "online");
            }

            if (lastSeenEpoch.HasValue && lastSeenEpoch.Value > 0)
            {
                // netstandard1.4: no DateTimeOffset.FromUnixTimeSeconds
                var lastSeenLocal = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(lastSeenEpoch.Value)
                    .ToLocalTime();
                var now = DateTime.Now;
                var timeStr = lastSeenLocal.ToString("HH:mm");

                if (lastSeenLocal.Date == now.Date)
                {
                    return string.Format(
                        _strings.Get("ChatDetail_PresenceLastSeenToday", "last seen today at {0}"),
                        timeStr);
                }

                if (lastSeenLocal.Date == now.Date.AddDays(-1))
                {
                    return string.Format(
                        _strings.Get("ChatDetail_PresenceLastSeenYesterday", "last seen yesterday at {0}"),
                        timeStr);
                }

                if (lastSeenLocal.Date > now.Date.AddDays(-7))
                {
                    return string.Format(
                        _strings.Get("ChatDetail_PresenceLastSeenWeekday", "last seen on {0} at {1}"),
                        lastSeenLocal.ToString("dddd"),
                        timeStr);
                }

                return string.Format(
                    _strings.Get("ChatDetail_PresenceLastSeenDate", "last seen {0} at {1}"),
                    lastSeenLocal.ToString("dd/MM/yyyy"),
                    timeStr);
            }

            return _strings.Get("ChatDetail_PresenceLastSeenRecently", "last seen recently");
        }

        private async Task SendMessageAsync()
        {
            if (string.IsNullOrWhiteSpace(MessageText) || _activeChat == null) return;

            string text = MessageText;
            IsSending = true;
            MessageText = string.Empty;

            try
            {
                await _messageService.SendTextMessageAsync(_activeChat.JID, text);
                MessageSent?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatDetailViewModel] Send error: {ex.Message}");
                MessageText = text;
            }
            finally
            {
                IsSending = false;
            }
        }

        private async Task AttachMediaAsync()
        {
            if (_activeChat == null || _isSending || _isRecording) return;

            string targetJid = _activeChat.JID;
            try
            {
                PickedChatMedia picked = await _filePicker.PickChatAttachmentAsync();
                if (picked == null || picked.Bytes == null || picked.Bytes.Length == 0)
                {
                    return;
                }

                if (picked.IsAudio)
                {
                    IsSending = true;
                    try
                    {
                        await _messageService.SendAudioMessageAsync(
                            targetJid,
                            picked.Bytes,
                            picked.MimeType ?? "audio/mp4",
                            durationSeconds: 0,
                            isVoiceMessage: false);
                        MessageSent?.Invoke(this, EventArgs.Empty);
                    }
                    finally
                    {
                        IsSending = false;
                    }

                    return;
                }

                if (!picked.IsImage)
                {
                    return;
                }

                string info = string.Format("{0} ({1} KB)", picked.FileName ?? "image", picked.Bytes.Length / 1024);
                bool confirmed = await _dialogs.ShowImageSendPreviewAsync(picked.Bytes, info);
                if (!confirmed || _activeChat == null)
                {
                    return;
                }

                string caption = string.IsNullOrWhiteSpace(MessageText) ? null : MessageText.Trim();
                IsSending = true;
                try
                {
                    await _messageService.SendImageAsync(targetJid, picked.Bytes, caption);
                    if (caption != null)
                    {
                        MessageText = string.Empty;
                    }

                    MessageSent?.Invoke(this, EventArgs.Empty);
                }
                finally
                {
                    IsSending = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Attach error: " + ex.Message);
                IsSending = false;
                await _dialogs.ShowMessageAsync(
                    _strings.Get("Toast_AppName", "Unison"),
                    _strings.Get("ChatDetail_AttachSendFailed", "Could not send the file."),
                    _strings.Get("Common_OK", "OK"));
            }
        }

        private async Task StartRecordingAsync()
        {
            if (_activeChat == null || _isSending || _isRecording) return;

            try
            {
                _recordingSession = await _audioRecording.StartAsync();
                _recordingChatJid = _activeChat.JID;
                RecordingElapsedText = "0:00";
                IsRecording = true;
                StartElapsedLoop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Record start error: " + ex.Message);
                await CancelRecordingCoreAsync();
                await _dialogs.ShowMessageAsync(
                    _strings.Get("Toast_AppName", "Unison"),
                    _strings.Get("ChatDetail_RecordFailed", "Could not record audio. Check microphone permission."),
                    _strings.Get("Common_OK", "OK"));
            }
        }

        private async Task StopAndSendRecordingAsync()
        {
            var session = _recordingSession ?? _audioRecording.Current;
            string targetJid = _recordingChatJid ?? _activeChat?.JID;

            StopElapsedLoop();

            if (session == null || !session.IsActive)
            {
                await ClearRecordingUiAsync();
                return;
            }

            AudioRecordingResult recording;
            try
            {
                recording = await session.StopAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Record stop error: " + ex.Message);
                await CancelRecordingCoreAsync();
                await _dialogs.ShowMessageAsync(
                    _strings.Get("Toast_AppName", "Unison"),
                    _strings.Get("ChatDetail_RecordFailed", "Could not record audio. Check microphone permission."),
                    _strings.Get("Common_OK", "OK"));
                return;
            }

            _recordingSession = null;
            _recordingChatJid = null;
            IsRecording = false;
            RecordingElapsedText = "0:00";

            if (string.IsNullOrWhiteSpace(targetJid))
            {
                await _dialogs.ShowMessageAsync(
                    _strings.Get("Toast_AppName", "Unison"),
                    _strings.Get("ChatDetail_RecordingChatUnavailable", "The conversation for this recording is no longer available."),
                    _strings.Get("Common_OK", "OK"));
                return;
            }

            IsSending = true;
            try
            {
                await _messageService.SendAudioMessageAsync(
                    targetJid,
                    recording.Bytes,
                    recording.MimeType ?? "audio/mp4",
                    recording.DurationSeconds,
                    recording.IsVoiceNote);
                MessageSent?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Voice send error: " + ex.Message);
                await _dialogs.ShowMessageAsync(
                    _strings.Get("Toast_AppName", "Unison"),
                    _strings.Get("ChatDetail_AudioSendFailed", "Could not send the audio."),
                    _strings.Get("Common_OK", "OK"));
            }
            finally
            {
                IsSending = false;
            }
        }

        private async Task CancelRecordingCoreAsync()
        {
            StopElapsedLoop();

            var session = _recordingSession ?? _audioRecording?.Current;
            _recordingSession = null;
            _recordingChatJid = null;

            try
            {
                if (session != null && session.IsActive)
                {
                    await session.CancelAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Cancel recording: " + ex.Message);
            }
            finally
            {
                await ClearRecordingUiAsync();
            }
        }

        private Task ClearRecordingUiAsync()
        {
            return _dispatcher.RunAsync(() =>
            {
                if (_isRecording)
                {
                    IsRecording = false;
                }

                RecordingElapsedText = "0:00";
            });
        }

        private void StartElapsedLoop()
        {
            StopElapsedLoop();
            _elapsedCts = new CancellationTokenSource();
            var token = _elapsedCts.Token;
            var session = _recordingSession;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested && session != null && session.IsActive)
                    {
                        string text = FormatElapsed(session.Elapsed);
                        await _dispatcher.RunAsync(() =>
                        {
                            if (_recordingSession == session && _isRecording)
                            {
                                RecordingElapsedText = text;
                            }
                        });
                        await Task.Delay(250, token);
                    }
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Elapsed loop: " + ex.Message);
                }
            }, token);
        }

        private void StopElapsedLoop()
        {
            try
            {
                _elapsedCts?.Cancel();
            }
            catch
            {
            }

            _elapsedCts?.Dispose();
            _elapsedCts = null;
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalHours >= 1)
            {
                return string.Format(
                    "{0}:{1:D2}:{2:D2}",
                    (int)elapsed.TotalHours,
                    elapsed.Minutes,
                    elapsed.Seconds);
            }

            return string.Format("{0}:{1:D2}", (int)elapsed.TotalMinutes, elapsed.Seconds);
        }

        private void AppendLiveMessages()
        {
            if (_activeChat == null) return;

            var live = _whatsAppService.GetLiveMessages(_activeChat.JID);
            if (live == null || live.Count == 0) return;

            var existingIds = new HashSet<string>(
                Messages.Where(m => m?.Id != null).Select(m => m.Id),
                StringComparer.Ordinal);

            foreach (var msg in live)
            {
                if (msg == null || string.IsNullOrEmpty(msg.Id)) continue;
                if (existingIds.Contains(msg.Id)) continue;
                Messages.Add(_messageFactory.Create(msg));
                existingIds.Add(msg.Id);
            }
        }
    }
}
