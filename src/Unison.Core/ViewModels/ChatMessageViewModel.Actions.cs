using System;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>Per-bubble media / pin actions (MVVM — not conversation-scoped).</summary>
    public partial class ChatMessageViewModel
    {
        /// <summary>Raised after a successful pin/unpin so the chat host can refresh chrome.</summary>
        public event EventHandler PinnedChanged;

        public async Task ShowReactionsAsync()
        {
            if (_dialogs == null || !HasReactions)
            {
                return;
            }

            try
            {
                await _dialogs.ShowReactionsDialogAsync(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatMessageViewModel] ShowReactionsAsync: " + ex.Message);
            }
        }

        public async Task<string> EnsureAudioReadyAsync(bool showErrorDialog = false)
        {
            if (!Model.IsAudio || _messages == null)
            {
                LogMedia("audio-ensure-skip", "no-service-or-not-audio");
                return null;
            }

            LogMedia("audio-ensure-start");
            try
            {
                string uri = await _messages.EnsureAudioAvailableAsync(Model);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Audio unavailable.");
                }

                LogMedia("audio-ensure-ok", "uri=" + uri);
                return uri;
            }
            catch (Exception ex)
            {
                LogMediaError("audio-ensure-fail", ex);
                if (showErrorDialog)
                {
                    await ShowErrorSafeAsync(
                        "ChatDetail_AudioPlayFailed",
                        "Could not play this audio.");
                }

                return null;
            }
        }

        public async Task DownloadAudioAsync()
        {
            if (!Model.IsAudio)
            {
                return;
            }

            bool isRetry = AudioPlaybackStatus == AudioPlaybackStatus.NotAvailable;
            if (Model.HasLocalAudio && !isRetry)
            {
                return;
            }

            LogMedia("audio-download-click", "retry=" + isRetry);
            AudioPlaybackStatus = AudioPlaybackStatus.Downloading;
            RaiseMediaCommandsChanged();
            await AllowUiPaintAsync();
            try
            {
                string uri = await EnsureAudioReadyAsync(showErrorDialog: false);
                if (!string.IsNullOrWhiteSpace(uri) && Model.HasLocalAudio)
                {
                    AudioPlaybackStatus = AudioPlaybackStatus.Ready;
                    LogMedia("audio-download-ok");
                }
                else
                {
                    MarkAudioUnavailable();
                    LogMedia("audio-download-empty");
                }
            }
            catch (Exception ex)
            {
                MarkAudioUnavailable();
                LogMediaError("audio-download-fail", ex);
            }
            finally
            {
                RaiseMediaCommandsChanged();
            }
        }

        public async Task<string> EnsureImageReadyAsync(bool showErrorDialog = true)
        {
            if ((!Model.IsImage && !Model.IsSticker) || _messages == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(Model.ImageUri))
            {
                return Model.ImageUri;
            }

            LogMedia(Model.IsSticker ? "sticker-ensure-start" : "image-ensure-start");
            try
            {
                string uri = await _messages.EnsureImageAvailableAsync(Model);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    if (Model.IsSticker)
                    {
                        Model.IsStickerFailed = true;
                    }

                    throw new InvalidOperationException("Image unavailable.");
                }

                LogMedia(Model.IsSticker ? "sticker-ensure-ok" : "image-ensure-ok", "uri=" + uri);
                return uri;
            }
            catch (Exception ex)
            {
                LogMediaError(Model.IsSticker ? "sticker-ensure-fail" : "image-ensure-fail", ex);
                if (Model.IsSticker)
                {
                    Model.IsStickerFailed = true;
                    return null;
                }

                if (showErrorDialog)
                {
                    await ShowErrorSafeAsync(
                        "ChatDetail_ImageDownloadFailed",
                        "Could not download this image.");
                }

                return null;
            }
        }

        public async Task DownloadImageAsync()
        {
            if (!NeedsImageDownload)
            {
                return;
            }

            LogMedia("image-download-click");
            IsImageDownloading = true;
            RaiseMediaCommandsChanged();
            await AllowUiPaintAsync();
            try
            {
                string uri = await EnsureImageReadyAsync(showErrorDialog: true);
                LogMedia(string.IsNullOrWhiteSpace(uri) ? "image-download-empty" : "image-download-ok");
            }
            finally
            {
                IsImageDownloading = false;
                RaiseMediaCommandsChanged();
            }
        }

        public async Task<string> EnsureVideoReadyAsync(bool showErrorDialog = true)
        {
            if (!Model.IsVideo || _messages == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(Model.VideoUri))
            {
                return Model.VideoUri;
            }

            LogMedia("video-ensure-start");
            try
            {
                string uri = await _messages.EnsureVideoAvailableAsync(Model);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Video unavailable.");
                }

                LogMedia("video-ensure-ok", "uri=" + uri);
                return uri;
            }
            catch (Exception ex)
            {
                LogMediaError("video-ensure-fail", ex);
                if (showErrorDialog)
                {
                    await ShowErrorSafeAsync(
                        "ChatDetail_VideoDownloadFailed",
                        "Could not download this video.");
                }

                return null;
            }
        }

        public async Task DownloadVideoAsync()
        {
            if (!NeedsVideoDownload)
            {
                return;
            }

            LogMedia("video-download-click");
            IsVideoDownloading = true;
            RaiseMediaCommandsChanged();
            await AllowUiPaintAsync();
            try
            {
                string uri = await EnsureVideoReadyAsync(showErrorDialog: true);
                LogMedia(string.IsNullOrWhiteSpace(uri) ? "video-download-empty" : "video-download-ok");
            }
            finally
            {
                IsVideoDownloading = false;
                RaiseMediaCommandsChanged();
            }
        }

        public async Task<string> EnsureDocumentReadyAsync(bool showErrorDialog = false)
        {
            if (!Model.IsDocument || _messages == null)
            {
                LogMedia("document-ensure-skip", "no-service-or-not-document");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(Model.DocumentUri))
            {
                return Model.DocumentUri;
            }

            LogMedia(
                "document-ensure-start",
                string.Format(
                    "hasKey={0}; hasUrl={1}; hasPath={2}; file={3}",
                    !string.IsNullOrWhiteSpace(Model.DocumentMediaKeyBase64),
                    !string.IsNullOrWhiteSpace(Model.DocumentUrl),
                    !string.IsNullOrWhiteSpace(Model.DocumentDirectPath),
                    Model.DocumentFileName ?? "?"));
            try
            {
                string uri = await _messages.EnsureDocumentAvailableAsync(Model);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Document unavailable.");
                }

                LogMedia("document-ensure-ok", "uri=" + uri);
                return uri;
            }
            catch (Exception ex)
            {
                LogMediaError("document-ensure-fail", ex);
                if (showErrorDialog)
                {
                    await ShowErrorSafeAsync(
                        "ChatDetail_DocumentDownloadFailed",
                        "Could not download this file.");
                }

                return null;
            }
        }

        /// <summary>
        /// Confirms (optional) then downloads. <c>true</c> ok, <c>false</c> fail, <c>null</c> cancelled.
        /// </summary>
        public async Task<bool?> DownloadDocumentAsync(bool confirmFirst)
        {
            if (!Model.IsDocument)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(Model.DocumentUri))
            {
                return true;
            }

            if (confirmFirst)
            {
                if (_dialogs == null)
                {
                    return false;
                }

                bool confirmed;
                try
                {
                    confirmed = await _dialogs.ShowConfirmAsync(
                        _strings?.Get("ChatDetail_DocumentDownloadTitle", "Download this file?") ?? "Download this file?",
                        _strings?.Get(
                            "ChatDetail_DocumentDownloadBody",
                            "The file will be saved locally so you can open it later.")
                            ?? "The file will be saved locally so you can open it later.",
                        _strings?.Get("ChatDetail_DocumentDownloadConfirm", "Download") ?? "Download",
                        _strings?.Get("ChatDetail_DocumentDownloadCancel", "Cancel") ?? "Cancel");
                }
                catch
                {
                    confirmed = false;
                }

                if (!confirmed)
                {
                    LogMedia("document-download-cancelled");
                    return null;
                }
            }

            LogMedia("document-download-click", "confirmFirst=" + confirmFirst + "; retry=" + _isDocumentDownloadFailed);
            IsDocumentDownloading = true;
            RaiseMediaCommandsChanged();
            // Let the ProgressRing paint before a fast failure returns.
            await AllowUiPaintAsync();
            try
            {
                string uri = await EnsureDocumentReadyAsync(showErrorDialog: false);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    MarkDocumentUnavailable();
                    LogMedia("document-download-empty");
                    return false;
                }

                LogMedia("document-download-ok");
                return true;
            }
            catch (Exception ex)
            {
                MarkDocumentUnavailable();
                LogMediaError("document-download-fail", ex);
                return false;
            }
            finally
            {
                IsDocumentDownloading = false;
                RaiseMediaCommandsChanged();
            }
        }

        public async Task DocumentPrimaryAsync()
        {
            if (!Model.IsDocument)
            {
                return;
            }

            if (HasLocalDocument)
            {
                await OpenDocumentAsync();
                return;
            }

            await DownloadDocumentAsync(confirmFirst: true);
        }

        public async Task OpenDocumentAsync()
        {
            if (!Model.IsDocument)
            {
                return;
            }

            // Overflow / ready menu: open the cached local file only (no download here).
            string uri = Model.DocumentUri;
            LogMedia("document-open-click", "hasLocal=" + HasLocalDocument);
            if (string.IsNullOrWhiteSpace(uri))
            {
                LogMedia("document-open-skip", "no-local-uri");
                await ShowErrorSafeAsync(
                    "ChatDetail_DocumentOpenFailed",
                    "Could not open this file.");
                return;
            }

            if (_uriLauncher == null)
            {
                LogMedia("document-open-skip", "no-launcher");
                return;
            }

            bool launched;
            try
            {
                launched = await _uriLauncher.LaunchLocalFileAsync(uri);
            }
            catch (Exception ex)
            {
                LogMediaError("document-open-fail", ex);
                launched = false;
            }

            if (!launched)
            {
                await ShowErrorSafeAsync(
                    "ChatDetail_DocumentOpenFailed",
                    "Could not open this file.");
            }
            else
            {
                LogMedia("document-open-ok");
            }
        }

        /// <summary>Ensures the document is in cache, then opens the save picker.</summary>
        public async Task ExportDocumentAsync()
        {
            if (!Model.IsDocument)
            {
                return;
            }

            if (!HasLocalDocument)
            {
                await DownloadDocumentAsync(confirmFirst: false);
            }

            if (HasLocalDocument)
            {
                await SaveDocumentAsAsync();
            }
        }

        public async Task SaveDocumentAsAsync()
        {
            if (!Model.IsDocument || _filePicker == null)
            {
                return;
            }

            // Copy the local cache to a user-chosen path via FileSavePicker.
            LogMedia("document-saveas-click");
            string uri = Model.DocumentUri;
            if (string.IsNullOrWhiteSpace(uri))
            {
                LogMedia("document-saveas-skip", "no-local-uri");
                await ShowErrorSafeAsync(
                    "ChatDetail_DocumentSaveFailed",
                    "Could not save this file.");
                return;
            }

            string suggested = !string.IsNullOrWhiteSpace(Model.DocumentFileName)
                ? Model.DocumentFileName.Trim()
                : ((_strings?.Get("ChatDetail_DocumentFallbackName", "Document") ?? "Document") + ".bin");

            try
            {
                await _filePicker.PickSaveLocalFileAsync(uri, suggested, Model.DocumentMimeType);
                LogMedia("document-saveas-ok", "suggested=" + suggested);
            }
            catch (Exception ex)
            {
                LogMediaError("document-saveas-fail", ex);
                await ShowErrorSafeAsync(
                    "ChatDetail_DocumentSaveFailed",
                    "Could not save this file.");
            }
        }

        /// <summary>Pin/unpin this message in its chat (uses <see cref="ChatMessage.RemoteJid"/>).</summary>
        public async Task SetPinnedAsync(bool pin, uint durationSeconds = 604800)
        {
            if (_messages == null || string.IsNullOrWhiteSpace(Model.Id))
            {
                return;
            }

            string chatJid = Model.RemoteJid;
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                LogMedia("pin-skip", "no-remote-jid");
                return;
            }

            LogMedia("pin-start", "pin=" + pin + "; duration=" + durationSeconds);
            try
            {
                await _messages.SetMessagePinnedAsync(chatJid, Model, pin, durationSeconds);
                PinnedChanged?.Invoke(this, EventArgs.Empty);
                LogMedia("pin-ok", "pin=" + pin);
            }
            catch (Exception ex)
            {
                LogMediaError("pin-fail", ex);
            }
        }

        /// <summary>Yield so bindings (ProgressRing) paint before a fast network/key failure returns.</summary>
        private static async Task AllowUiPaintAsync()
        {
            await Task.Yield();
            // One frame ≈ paint ProgressRing visibility/IsActive flip on retry.
            await Task.Delay(16);
        }

        private async Task ShowErrorSafeAsync(string resourceKey, string fallback)
        {
            if (_dialogs == null)
            {
                return;
            }

            try
            {
                await _dialogs.ShowMessageAsync(
                    _strings?.Get("Toast_AppName", "Unison") ?? "Unison",
                    _strings?.Get(resourceKey, fallback) ?? fallback,
                    _strings?.Get("Common_OK", "OK") ?? "OK");
            }
            catch
            {
            }
        }

        private void LogMedia(string eventName, string details = null)
        {
            string id = Model?.Id ?? "?";
            string kind = Model != null ? Model.Kind.ToString() : "?";
            string payload = string.Format(
                "id={0}; kind={1}{2}",
                id,
                kind,
                string.IsNullOrWhiteSpace(details) ? string.Empty : "; " + details);

            try
            {
                _diagnostics?.Write("media", eventName, payload);
            }
            catch
            {
            }

            try
            {
                _sessionLogger?.WriteAlways("[media/" + eventName + "] " + payload);
            }
            catch
            {
            }

            System.Diagnostics.Debug.WriteLine("[media/" + eventName + "] " + payload);
        }

        private void LogMediaError(string eventName, Exception ex)
        {
            string detail = ex == null ? null : (ex.GetType().Name + ": " + ex.Message);
            LogMedia(eventName, detail);
            try
            {
                _sessionLogger?.WriteErrorAlways("[media/" + eventName + "]", ex);
            }
            catch
            {
            }

            try
            {
                if (ex != null)
                {
                    _diagnostics?.RecordException("media", eventName, ex, "id=" + (Model?.Id ?? "?"));
                }
            }
            catch
            {
            }
        }

        private void RaiseMediaCommandsChanged()
        {
            (DownloadAudioCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DownloadImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DownloadVideoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DocumentPrimaryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DownloadDocumentCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenDocumentCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveDocumentAsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportDocumentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
