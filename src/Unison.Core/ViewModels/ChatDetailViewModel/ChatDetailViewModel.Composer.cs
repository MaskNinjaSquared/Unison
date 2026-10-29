using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Exceptions;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        private void RaiseComposerCommandsChanged()
        {
            OnPropertyChanged(nameof(CanCompose));
            RaiseSendMessageCanExecuteChanged();
            (AttachMediaCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AttachAudioCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AttachCameraCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AttachFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PasteClipboardCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SendRecordingCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleStickerBoxCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (InsertEmojiCommand as RelayCommand<string>)?.RaiseCanExecuteChanged();
        }

        private void RaiseSendMessageCanExecuteChanged() =>
            (SendMessageCommand as RelayCommand)?.RaiseCanExecuteChanged();

        private void RaisePinToStartCanExecuteChanged()
        {
            (PinToStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteChatCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ArchiveChatCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (UnarchiveChatCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseMuteCommandsCanExecuteChanged();
        }

        private async Task SendMessageAsync()
        {
            if (string.IsNullOrWhiteSpace(MessageText) || _activeChat == null) return;

            string jid = _activeChat.JID;
            OutgoingTextMentions wire = _mentions.GetMessage();
            if (ComposerReply != null)
            {
                wire.Quote = OutgoingQuote.From(ComposerReply.Model, jid);
            }
            string text = _mentions.Text;
            IsSending = true;
            _composerHeld = true;
            _messageText = string.Empty;
            OnPropertyChanged(nameof(MessageText));
            CloseMentionMenu();
            DismissReply();
            RaiseSendMessageCanExecuteChanged();
            // Optimistic bubble is queued inside the service before transport completes —
            // ask the view to stick to bottom immediately.
            MessageSent?.Invoke(this, EventArgs.Empty);
            LogSend("text-start", "jid=" + jid + "; chars=" + text.Length);

            try
            {
                await _messageService.SendTextMessageAsync(jid, wire);
                _mentions.Clear();
                _composerHeld = false;
                if (!string.IsNullOrEmpty(_messageText))
                {
                    _mentions.SetText(_messageText);
                }

                RecordSentEmojis(text);
                MessageSent?.Invoke(this, EventArgs.Empty);
                _uiSounds?.PlayMessageSent();
                LogSend("text-ok", "jid=" + jid);
            }
            catch (UnisonUserException ex)
            {
                _composerHeld = false;
                MessageText = text;
                await PresentUserExceptionAsync(ex, "text-fail");
            }
            catch (Exception ex)
            {
                _composerHeld = false;
                MessageText = text;
                await PresentUserExceptionAsync(
                    new TextSendException("SendText failed: " + ex.Message, ex),
                    "text-fail");
            }
            finally
            {
                _composerHeld = false;
                IsSending = false;
            }
        }

        private Task AttachMediaAsync() => AttachImagesAsync();

        private Task AttachAudioAsync() =>
            AttachAsync(() => _filePicker.PickChatAudioAsync(), SendPickedAudioAsync, "audio");

        private Task AttachDocumentAsync() =>
            AttachAsync(() => _filePicker.PickChatDocumentAsync(), SendPickedDocumentAsync, "document");

        /// <summary>
        /// Feeds a camera JPEG into the same image-send preview used by gallery attach.
        /// Called by the chat detail view after <see cref="CameraPickViewModel.PhotoCaptured"/>.
        /// </summary>
        public Task AcceptCapturedPhotoAsync(byte[] jpegBytes)
        {
            if (jpegBytes == null || jpegBytes.Length == 0 || _activeChat == null)
            {
                return Task.CompletedTask;
            }

            return SendPickedImageAsync(
                _activeChat.JID,
                new PickedChatMedia
                {
                    Bytes = jpegBytes,
                    MimeType = "image/jpeg",
                    FileName = "camera.jpg",
                    IsImage = true
                });
        }

        /// <summary>
        /// Picks a file of one kind and sends it, with the guards and the failure reporting that
        /// every attachment shares.
        /// </summary>
        /// <remarks>
        /// One picker per kind, rather than the single mixed one this used to open. The old picker
        /// accepted pictures and audio together and then read the extension back off the chosen
        /// file to decide which it had been handed - a guess it only had to make because the user
        /// had not been asked. Now the menu item is the answer.
        /// </remarks>
        private async Task AttachAsync(
            Func<Task<PickedChatMedia>> pick,
            Func<string, PickedChatMedia, Task> send,
            string kind)
        {
            if (_activeChat == null || _isSending || _isRecording) return;

            string targetJid = _activeChat.JID;
            try
            {
                PickedChatMedia picked = await pick();
                if (picked == null || picked.Bytes == null || picked.Bytes.Length == 0)
                {
                    LogSend("attach-cancelled", "kind=" + kind);
                    return;
                }

                await send(targetJid, picked);
            }
            catch (UnisonUserException ex)
            {
                IsSending = false;
                await PresentUserExceptionAsync(ex, "attach-fail");
            }
            catch (Exception ex)
            {
                IsSending = false;
                await PresentUserExceptionAsync(
                    new AttachmentSendException("Attach failed: " + ex.Message, ex),
                    "attach-fail");
            }
        }

        private async Task AttachImagesAsync()
        {
            if (_activeChat == null || _isSending || _isRecording)
            {
                return;
            }

            string targetJid = _activeChat.JID;
            try
            {
                IReadOnlyList<PickedChatMedia> picked = await _filePicker.PickChatImagesAsync();
                if (picked == null || picked.Count == 0)
                {
                    LogSend("attach-cancelled", "kind=image");
                    return;
                }

                await SendPickedImagesAsync(targetJid, picked);
            }
            catch (UnisonUserException ex)
            {
                IsSending = false;
                await PresentUserExceptionAsync(ex, "attach-fail");
            }
            catch (Exception ex)
            {
                IsSending = false;
                await PresentUserExceptionAsync(
                    new AttachmentSendException("Attach failed: " + ex.Message, ex),
                    "attach-fail");
            }
        }

        private async Task SendPickedAudioAsync(string targetJid, PickedChatMedia picked)
        {
            IsSending = true;
            MessageSent?.Invoke(this, EventArgs.Empty);
            LogSend("audio-attach-start", "bytes=" + picked.Bytes.Length);
            try
            {
                await _messageService.SendAudioMessageAsync(
                    targetJid,
                    picked.Bytes,
                    picked.MimeType ?? "audio/mp4",
                    durationSeconds: 0,
                    isVoiceMessage: false);
                MessageSent?.Invoke(this, EventArgs.Empty);
                _uiSounds?.PlayMessageSent();
                LogSend("audio-attach-ok");
            }
            catch (UnisonUserException ex)
            {
                await PresentUserExceptionAsync(ex, "audio-attach-fail");
            }
            catch (Exception ex)
            {
                await PresentUserExceptionAsync(
                    new AudioSendException("Attach audio failed: " + ex.Message, ex),
                    "audio-attach-fail");
            }
            finally
            {
                IsSending = false;
            }
        }

        private async Task SendPickedDocumentAsync(string targetJid, PickedChatMedia picked)
        {
            if (!picked.IsDocument)
            {
                await PresentUserExceptionAsync(
                    new AttachmentSendException("Unsupported attachment type."),
                    "attach-unsupported");
                return;
            }

            string info = string.Format(
                "{0} ({1} KB)",
                picked.FileName ?? "document",
                picked.Bytes.Length / 1024);
            string seedCaption = string.IsNullOrWhiteSpace(MessageText) ? null : MessageText.Trim();
            ImageSendPreviewResult preview = await _dialogs.ShowDocumentSendPreviewAsync(
                picked.FileName,
                info,
                seedCaption);
            if (preview == null || !preview.Confirmed || _activeChat == null)
            {
                LogSend("document-attach-cancelled");
                return;
            }

            string caption = string.IsNullOrWhiteSpace(preview.Caption) ? null : preview.Caption.Trim();
            IsSending = true;
            MessageSent?.Invoke(this, EventArgs.Empty);
            LogSend("document-attach-start", "bytes=" + picked.Bytes.Length);
            try
            {
                await _messageService.SendDocumentAsync(
                    targetJid,
                    picked.Bytes,
                    picked.MimeType,
                    picked.FileName,
                    caption);
                if (!string.IsNullOrWhiteSpace(caption) &&
                    string.Equals(MessageText?.Trim(), seedCaption, StringComparison.Ordinal))
                {
                    MessageText = string.Empty;
                }

                MessageSent?.Invoke(this, EventArgs.Empty);
                _uiSounds?.PlayMessageSent();
                LogSend("document-attach-ok");
            }
            catch (UnisonUserException ex)
            {
                await PresentUserExceptionAsync(ex, "document-attach-fail");
            }
            catch (Exception ex)
            {
                await PresentUserExceptionAsync(
                    new AttachmentSendException("Attach document failed: " + ex.Message, ex),
                    "document-attach-fail");
            }
            finally
            {
                IsSending = false;
            }
        }

        private async Task SendPickedImageAsync(string targetJid, PickedChatMedia picked)
        {
            await SendPickedImagesAsync(targetJid, new[] { picked });
        }

        private async Task SendPickedImagesAsync(string targetJid, IReadOnlyList<PickedChatMedia> picked)
        {
            if (picked == null || picked.Count == 0)
            {
                return;
            }

            for (int i = 0; i < picked.Count; i++)
            {
                if (picked[i] == null ||
                    !picked[i].IsImage ||
                    picked[i].Bytes == null ||
                    picked[i].Bytes.Length == 0)
                {
                    await PresentUserExceptionAsync(
                        new AttachmentSendException("Unsupported attachment type."),
                        "attach-unsupported");
                    return;
                }
            }

            long totalBytes = picked.Sum(image => (long)image.Bytes.Length);
            string batchInfoFormat = _strings != null
                ? _strings.Get("ChatDetail_ImageBatchInfo", "{0} images ({1} KB)")
                : "{0} images ({1} KB)";
            string info = picked.Count == 1
                ? string.Format(
                    "{0} ({1} KB)",
                    picked[0].FileName ?? "image",
                    picked[0].Bytes.Length / 1024)
                : string.Format(
                    batchInfoFormat,
                    picked.Count,
                    totalBytes / 1024);
            string seedCaption = string.IsNullOrWhiteSpace(MessageText) ? null : MessageText.Trim();
            ImageSendPreviewResult preview = await _dialogs.ShowImageSendPreviewAsync(
                picked[0].Bytes,
                info,
                seedCaption);
            if (preview == null || !preview.Confirmed || _activeChat == null)
            {
                LogSend("image-attach-cancelled");
                return;
            }

            string caption = string.IsNullOrWhiteSpace(preview.Caption) ? null : preview.Caption.Trim();
            IsSending = true;
            MessageSent?.Invoke(this, EventArgs.Empty);
            LogSend("image-attach-start", "count=" + picked.Count + "; bytes=" + totalBytes);
            try
            {
                await _messageService.SendImagesAsync(
                    targetJid,
                    picked.Select(image => image.Bytes).ToList(),
                    caption);
                if (!string.IsNullOrWhiteSpace(caption) &&
                    string.Equals(MessageText?.Trim(), seedCaption, StringComparison.Ordinal))
                {
                    MessageText = string.Empty;
                }

                MessageSent?.Invoke(this, EventArgs.Empty);
                _uiSounds?.PlayMessageSent();
                LogSend("image-attach-ok");
            }
            catch (UnisonUserException ex)
            {
                await PresentUserExceptionAsync(ex, "image-attach-fail");
            }
            catch (Exception ex)
            {
                await PresentUserExceptionAsync(
                    new ImageSendException("Attach image failed: " + ex.Message, ex),
                    "image-attach-fail");
            }
            finally
            {
                IsSending = false;
            }
        }

        /// <summary>
        /// Applies a Share Target payload to the open conversation: seeds the composer for text,
        /// and runs the existing attach previews for media.
        /// </summary>
        public async Task ApplyIncomingShareAsync(IncomingSharePayload payload)
        {
            if (payload == null || payload.IsEmpty || _activeChat == null)
            {
                return;
            }

            string targetJid = _activeChat.JID;
            if (string.IsNullOrWhiteSpace(targetJid))
            {
                return;
            }

            if (payload.HasText)
            {
                MessageText = payload.Text.Trim();
            }

            if (!payload.HasMedia)
            {
                return;
            }

            await SendPickedMediaBatchAsync(targetJid, payload.Media);
        }

        /// <summary>
        /// Sync peek for Ctrl+V: true when clipboard advertises media and paste can run.
        /// Starts <see cref="PasteClipboardCommand"/> when true (caller marks KeyDown Handled).
        /// </summary>
        public bool TryBeginPasteClipboardMedia()
        {
            if (!CanCompose || _copy == null || !_copy.HasPasteableMedia())
            {
                return false;
            }

            if (PasteClipboardCommand == null || !PasteClipboardCommand.CanExecute(null))
            {
                return false;
            }

            PasteClipboardCommand.Execute(null);
            return true;
        }

        /// <summary>
        /// Ctrl+V when the clipboard holds Image / Audio / Document bytes (not plain text).
        /// </summary>
        private async Task PasteClipboardAsync()
        {
            if (_copy == null || _activeChat == null || _isSending || _isRecording)
            {
                return;
            }

            string targetJid = _activeChat.JID;
            if (string.IsNullOrWhiteSpace(targetJid))
            {
                return;
            }

            ClipboardPasteResult paste;
            try
            {
                paste = await _copy.ReadPasteAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetail] PasteClipboard read: " + ex.Message);
                return;
            }

            if (paste == null || !paste.HasMedia)
            {
                return;
            }

            try
            {
                await SendPickedMediaBatchAsync(targetJid, paste.Media);
            }
            catch (UnisonUserException ex)
            {
                IsSending = false;
                await PresentUserExceptionAsync(ex, "paste-fail");
            }
            catch (Exception ex)
            {
                IsSending = false;
                await PresentUserExceptionAsync(
                    new AttachmentSendException("Paste failed: " + ex.Message, ex),
                    "paste-fail");
            }
        }

        /// <summary>
        /// Routes a mixed media list the same way Share Target does (images batched; audio/doc one-by-one).
        /// </summary>
        private async Task SendPickedMediaBatchAsync(string targetJid, IReadOnlyList<PickedChatMedia> media)
        {
            if (string.IsNullOrWhiteSpace(targetJid) || media == null || media.Count == 0)
            {
                return;
            }

            var images = new List<PickedChatMedia>();
            foreach (PickedChatMedia item in media)
            {
                if (item == null)
                {
                    continue;
                }

                if (item.IsImage)
                {
                    images.Add(item);
                    continue;
                }

                if (item.IsAudio)
                {
                    await SendPickedAudioAsync(targetJid, item);
                    continue;
                }

                if (item.IsDocument)
                {
                    await SendPickedDocumentAsync(targetJid, item);
                }
            }

            if (images.Count > 0)
            {
                await SendPickedImagesAsync(targetJid, images);
            }
        }

        private int _recordingCueGeneration;

        private async Task StartRecordingAsync()
        {
            if (_activeChat == null || _isSending || _isRecording) return;

            // Collapse the growing composer back to one line before the recording overlay
            // takes over (audio flow may change later; for now clear is enough).
            if (!string.IsNullOrEmpty(MessageText))
            {
                MessageText = string.Empty;
            }

            string chatJid = _activeChat.JID;
            try
            {
                if (_audioRecording == null ||
                    !await _audioRecording.EnsureMicrophoneAccessAsync())
                {
                    _uiSounds?.PlayVoiceNoteError();
                    await ShowSimpleErrorAsync(
                        "ChatDetail_RecordFailed",
                        "Could not record audio. Check microphone permission.");
                    return;
                }
            }
            catch (Exception ex)
            {
                LogSendError("record-permission-fail", ex);
                _uiSounds?.PlayVoiceNoteError();
                await ShowSimpleErrorAsync(
                    "ChatDetail_RecordFailed",
                    "Could not record audio. Check microphone permission.");
                return;
            }

            int generation = ++_recordingCueGeneration;
            _uiSounds?.PlayVoiceNoteStart();
            await Task.Delay(VoiceNoteCueTiming.StartRecordingDelayMs);
            if (generation != _recordingCueGeneration ||
                _isSending ||
                _isRecording ||
                _activeChat == null ||
                !string.Equals(_activeChat.JID, chatJid, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                LogSend("record-start");
                _recordingSession = await _audioRecording.StartAsync();
                _recordingChatJid = chatJid;
                RecordingElapsedText = "0:00";
                IsRecording = true;
                StartElapsedLoop();
            }
            catch (Exception ex)
            {
                LogSendError("record-start-fail", ex);
                _uiSounds?.PlayVoiceNoteError();
                await CancelRecordingCoreAsync();
                await ShowSimpleErrorAsync(
                    "ChatDetail_RecordFailed",
                    "Could not record audio. Check microphone permission.");
            }
        }

        private async Task StopAndSendRecordingAsync()
        {
            _recordingCueGeneration++;
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
                _ = PlayStopCueAfterDelayAsync();
            }
            catch (Exception ex)
            {
                LogSendError("record-stop-fail", ex);
                _uiSounds?.PlayVoiceNoteError();
                await CancelRecordingCoreAsync();
                await ShowSimpleErrorAsync(
                    "ChatDetail_RecordFailed",
                    "Could not record audio. Check microphone permission.");
                return;
            }

            _recordingSession = null;
            _recordingChatJid = null;
            IsRecording = false;
            RecordingElapsedText = "0:00";

            if (string.IsNullOrWhiteSpace(targetJid))
            {
                await ShowSimpleErrorAsync(
                    "ChatDetail_RecordingChatUnavailable",
                    "The conversation for this recording is no longer available.");
                return;
            }

            IsSending = true;
            MessageSent?.Invoke(this, EventArgs.Empty);
            LogSend("voice-send-start", "bytes=" + (recording.Bytes?.Length ?? 0));
            try
            {
                await _messageService.SendAudioMessageAsync(
                    targetJid,
                    recording.Bytes,
                    recording.MimeType ?? "audio/ogg; codecs=opus",
                    recording.DurationSeconds,
                    recording.IsVoiceNote);
                MessageSent?.Invoke(this, EventArgs.Empty);
                _uiSounds?.PlayMessageSent();
                LogSend("voice-send-ok");
            }
            catch (UnisonUserException ex)
            {
                await PresentUserExceptionAsync(ex, "voice-send-fail");
            }
            catch (Exception ex)
            {
                await PresentUserExceptionAsync(
                    new AudioSendException("Voice send failed: " + ex.Message, ex),
                    "voice-send-fail");
            }
            finally
            {
                IsSending = false;
            }
        }

        /// <summary>Outer safety net so async command exceptions never tear down the app.</summary>
        private async Task RunSafeAsync(Func<Task> action, string op)
        {
            try
            {
                await action();
            }
            catch (UnisonUserException ex)
            {
                await PresentUserExceptionAsync(ex, op + "-unhandled");
            }
            catch (Exception ex)
            {
                LogSendError(op + "-unhandled", ex);
                await ShowSimpleErrorAsync(
                    "ChatDetail_AttachSendFailed",
                    "Could not complete that action.");
            }
        }

        private async Task PresentUserExceptionAsync(UnisonUserException ex, string logEvent)
        {
            if (ex == null)
            {
                return;
            }

            LogSendError(logEvent, ex);
            await ShowSimpleErrorAsync(ex.ResourceKey, ex.FallbackMessage);
        }

        private async Task ShowSimpleErrorAsync(string resourceKey, string fallback)
        {
            if (_dialogs == null)
            {
                return;
            }

            try
            {
                await _dialogs.ShowMessageAsync(
                    _strings != null ? _strings.Get("Toast_AppName", "Unison") : "Unison",
                    _strings != null ? _strings.Get(resourceKey, fallback) : fallback,
                    _strings != null ? _strings.Get("Common_OK", "OK") : "OK");
            }
            catch (Exception dialogEx)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] dialog failed: " + dialogEx.Message);
            }
        }

        private void LogSend(string eventName, string details = null)
        {
            string jid = _activeChat?.JID ?? "?";
            string payload = string.Format(
                "jid={0}{1}",
                jid,
                string.IsNullOrWhiteSpace(details) ? string.Empty : "; " + details);

            try
            {
                _diagnostics?.Write("send", eventName, payload);
            }
            catch
            {
            }

            try
            {
                _sessionLogger?.WriteAlways("[send/" + eventName + "] " + payload);
            }
            catch
            {
            }

            System.Diagnostics.Debug.WriteLine("[send/" + eventName + "] " + payload);
        }

        private void LogSendError(string eventName, Exception ex)
        {
            string detail = ex == null ? null : (ex.GetType().Name + ": " + ex.Message);
            LogSend(eventName, detail);
            try
            {
                _sessionLogger?.WriteErrorAlways("[send/" + eventName + "]", ex);
            }
            catch
            {
            }

            try
            {
                if (ex != null)
                {
                    _diagnostics?.RecordException("send", eventName, ex, "jid=" + (_activeChat?.JID ?? "?"));
                }
            }
            catch
            {
            }
        }

        private async Task PlayStopCueAfterDelayAsync()
        {
            int generation = _recordingCueGeneration;
            await Task.Delay(VoiceNoteCueTiming.StopSoundDelayMs);
            if (generation != _recordingCueGeneration)
            {
                return;
            }

            _uiSounds?.PlayVoiceNoteStop();
        }

        private async Task CancelRecordingCoreAsync()
        {
            _recordingCueGeneration++;
            StopElapsedLoop();

            var session = _recordingSession ?? _audioRecording?.Current;
            _recordingSession = null;
            _recordingChatJid = null;

            try
            {
                if (session != null && session.IsActive)
                {
                    await session.CancelAsync();
                    _uiSounds?.PlayVoiceNoteStop();
                }
            }
            catch (Exception ex)
            {
                LogSendError("record-cancel", ex);
                _uiSounds?.PlayVoiceNoteError();
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

        /// <summary>Inserts one row at its chronological position (live append / load-more).</summary>
        public ChatMessageViewModel InsertTimelineMessage(ChatMessage message)
        {
            if (message == null || _messageFactory == null)
            {
                return null;
            }

            var vm = CreateMessageVm(message);
            if (vm == null)
            {
                return null;
            }

            int index = _messageService.FindTimelineInsertIndex(
                Messages.Count,
                i => Messages[i]?.Timestamp ?? DateTime.MinValue,
                i => Messages[i]?.Id,
                message.Timestamp,
                message.Id);
            Messages.Insert(index, vm);
            return vm;
        }

        /// <summary>
        /// One pass over the visible timeline: run chrome, date chips, sender labels, and group
        /// author avatars. The bubble only binds the resolved <see cref="ChatMessage.ContactUri"/>.
        /// Mentions refresh stays here (needs bubble VMs).
    }
}
