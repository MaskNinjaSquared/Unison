using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Client;
using Unison.Uwp.Helpers;
using Windows.Storage;

namespace Unison.Uwp.Services.WhatsApp.Messages
{
    /// <summary>
    /// On-demand download of a message the user opened. Image, sticker, audio, video and
    /// document share one gate with the live hydrate path, so two taps cannot fetch at once
    /// on a phone.
    /// </summary>
    internal sealed class MediaAvailability
    {
        private readonly IWhatsAppService _client;
        private readonly IMediaDownloadHost _host;
        private readonly IJidResolver _jids;
        private readonly IMediaCache _cache;
        private readonly MediaDerivationService _derivation;
        private readonly MediaDownloadGate _gate;

        public MediaAvailability(
            IWhatsAppService client,
            IMediaDownloadHost host,
            IJidResolver jids,
            IMediaCache cache,
            MediaDerivationService derivation,
            MediaDownloadGate gate)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _derivation = derivation ?? throw new ArgumentNullException(nameof(derivation));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        }

        public async Task<string> EnsureAudioAsync(ChatMessage message)
        {
            if (message == null || !message.IsAudio) return null;
            if (!string.IsNullOrWhiteSpace(message.AudioUri))
            {
                try
                {
                    SessionLogger.Instance.WriteAlways(
                        "[Audio/ensure] cache-hit id=" + (message.Id ?? "?") + " uri=" + message.AudioUri);
                }
                catch
                {
                }

                return await EnsurePlayableAudioUriAsync(message, message.AudioUri);
            }

            await _client.EnsureConnectedAsync();

            MediaDownloadPlan plan = MediaDownloadPlan.For(message, MediaDownloadKind.Audio);
            if (!plan.HasKey) throw new InvalidOperationException("A chave do áudio não está disponível.");

            try
            {
                SessionLogger.Instance.WriteAlways(string.Format(
                    "[Audio/ensure] download-start id={0} mime={1} hasUrl={2} hasPath={3} keyLen={4}",
                    message.Id ?? "?",
                    plan.MimeType ?? "?",
                    !string.IsNullOrWhiteSpace(plan.Url),
                    !string.IsNullOrWhiteSpace(plan.DirectPath),
                    plan.MediaKey.Length));
            }
            catch
            {
            }

            await _gate.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.AudioUri))
                {
                    return await EnsurePlayableAudioUriAsync(message, message.AudioUri);
                }

                string cached = await TryReuseDownloadedFileAsync(
                    MediaCacheKind.Audio,
                    plan,
                    MediaFileExtensions.ForAudio(plan.MimeType));
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    message.AudioUri = cached;
                    SessionLogger.Instance.WriteAlways(
                        "[Audio/ensure] disk-hit id=" + (message.Id ?? "?") + " uri=" + cached);
                    await RememberAsync(message);
                    return await EnsurePlayableAudioUriAsync(message, cached);
                }

                var bytes = await _host.DownloadAndDecryptAsync(
                    plan.Url,
                    plan.DirectPath,
                    plan.MediaKey,
                    plan.MediaType,
                    plan.ExpectedSha256);

                string uri = await _cache.SaveAsync(
                    MediaCacheKind.Audio,
                    plan.FileBase,
                    MediaFileExtensions.ForAudio(plan.MimeType),
                    bytes,
                    reuseExisting: false);
                message.AudioUri = uri;
                try
                {
                    SessionLogger.Instance.WriteAlways(string.Format(
                        "[Audio/ensure] download-ok id={0} bytes={1} uri={2}",
                        message.Id ?? "?",
                        bytes != null ? bytes.Length : 0,
                        uri ?? "?"));
                }
                catch
                {
                }

                uri = await EnsurePlayableAudioUriAsync(message, uri);
                await RememberAsync(message);
                return uri;
            }
            catch (Exception ex)
            {
                try
                {
                    SessionLogger.Instance.WriteErrorAlways(
                        "[Audio/ensure] download-fail id=" + (message.Id ?? "?"),
                        ex);
                }
                catch
                {
                }

                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> EnsureImageAsync(ChatMessage message)
        {
            if (message == null) return null;
            bool isSticker = message.Kind == ChatMessageKind.Sticker;
            if (!message.IsImage && !isSticker) return null;
            if (!string.IsNullOrWhiteSpace(message.ImageUri))
            {
                return await EnsureWebPDisplayUriAsync(message);
            }

            await _client.EnsureConnectedAsync();

            MediaDownloadPlan plan = MediaDownloadPlan.For(
                message,
                isSticker ? MediaDownloadKind.Sticker : MediaDownloadKind.Image);
            if (!plan.HasKey)
            {
                if (isSticker)
                {
                    message.IsStickerFailed = true;
                    return null;
                }

                throw new InvalidOperationException("A chave da imagem não está disponível.");
            }

            await _gate.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.ImageUri))
                {
                    return await EnsureWebPDisplayUriAsync(message);
                }

                string cachedImage = await TryReuseDownloadedFileAsync(
                    MediaCacheKind.Image,
                    plan,
                    MediaFileExtensions.ForImage(plan.MimeType));
                if (!string.IsNullOrWhiteSpace(cachedImage))
                {
                    message.ImageUri = cachedImage;
                    if (isSticker)
                    {
                        message.IsStickerFailed = false;
                    }

                    await RememberAsync(message);
                    return await EnsureWebPDisplayUriAsync(message);
                }

                var bytes = await _host.DownloadAndDecryptAsync(
                    plan.Url,
                    plan.DirectPath,
                    plan.MediaKey,
                    plan.MediaType,
                    plan.ExpectedSha256);
                string uri = await SaveImageBytesForDisplayAsync(bytes, plan.FileBase, plan.MimeType);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    if (isSticker)
                    {
                        message.IsStickerFailed = true;
                        return null;
                    }

                    throw new InvalidOperationException("Falha ao guardar a imagem.");
                }

                message.ImageUri = uri;
                if (isSticker)
                {
                    message.IsStickerFailed = false;
                }

                await RememberAsync(message);
                return uri;
            }
            catch (Exception)
            {
                if (isSticker)
                {
                    message.IsStickerFailed = true;
                    return null;
                }

                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> EnsureVideoAsync(ChatMessage message)
        {
            if (message == null || !message.IsVideo) return null;
            if (!string.IsNullOrWhiteSpace(message.VideoUri))
            {
                return await EnsureVideoPosterAsync(message);
            }

            await _client.EnsureConnectedAsync();

            MediaDownloadPlan plan = MediaDownloadPlan.For(message, MediaDownloadKind.Video);
            if (!plan.HasKey)
            {
                throw new InvalidOperationException("A chave do vídeo não está disponível.");
            }

            await _gate.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.VideoUri))
                {
                    return await EnsureVideoPosterAsync(message);
                }

                string cachedVideo = await TryReuseDownloadedFileAsync(
                    MediaCacheKind.Video,
                    plan,
                    MediaFileExtensions.ForVideo(plan.MimeType));
                if (!string.IsNullOrWhiteSpace(cachedVideo))
                {
                    message.VideoUri = cachedVideo;
                    return await EnsureVideoPosterAsync(message);
                }

                var bytes = await _host.DownloadAndDecryptAsync(
                    plan.Url,
                    plan.DirectPath,
                    plan.MediaKey,
                    plan.MediaType,
                    plan.ExpectedSha256);
                string uri = await _cache.SaveAsync(
                    MediaCacheKind.Video,
                    plan.FileBase,
                    MediaFileExtensions.ForVideo(plan.MimeType),
                    bytes);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Falha ao guardar o vídeo.");
                }

                message.VideoUri = uri;
                try
                {
                    message.VideoPosterUri = await _derivation.TryCreateVideoPosterAsync(uri, message.Id ?? plan.FileBase);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Video poster failed: " + ex.Message);
                }

                await RememberAsync(message);
                return uri;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> EnsureDocumentAsync(ChatMessage message)
        {
            if (message == null || !message.IsDocument)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(message.DocumentUri))
            {
                await TryFillDocumentFileLengthFromLocalAsync(message);
                return message.DocumentUri;
            }

            await _client.EnsureConnectedAsync();

            MediaDownloadPlan plan = MediaDownloadPlan.For(message, MediaDownloadKind.Document);
            if (!plan.HasKey)
            {
                throw new InvalidOperationException("A chave do documento não está disponível.");
            }

            await _gate.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(message.DocumentUri))
                {
                    return message.DocumentUri;
                }

                string cachedDocument = await TryReuseDownloadedFileAsync(
                    MediaCacheKind.Document,
                    plan,
                    MediaFileExtensions.ForDocument(plan.FileName, plan.MimeType));
                if (!string.IsNullOrWhiteSpace(cachedDocument))
                {
                    message.DocumentUri = cachedDocument;
                    await TryFillDocumentFileLengthFromLocalAsync(message);
                    await RememberAsync(message);
                    return cachedDocument;
                }

                var bytes = await _host.DownloadAndDecryptAsync(
                    plan.Url,
                    plan.DirectPath,
                    plan.MediaKey,
                    plan.MediaType,
                    plan.ExpectedSha256);
                string uri = await _cache.SaveAsync(
                    MediaCacheKind.Document,
                    plan.FileBase,
                    MediaFileExtensions.ForDocument(plan.FileName, plan.MimeType),
                    bytes,
                    reuseExisting: false);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Falha ao guardar o documento.");
                }

                message.DocumentUri = uri;
                if (message.DocumentFileLengthBytes <= 0 && bytes != null && bytes.Length > 0)
                {
                    message.DocumentFileLengthBytes = bytes.Length;
                }

                await RememberAsync(message);
                return uri;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task RememberAsync(ChatMessage message)
        {
            string chatJid = _jids.GetCanonicalJid(message.RemoteJid);
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            await _host.SaveMessageQuietAsync(chatJid, message);
            _host.NotifyMessagesChanged(chatJid);
        }

        private async Task<string> EnsureWebPDisplayUriAsync(ChatMessage message)
        {
            if (message == null)
            {
                return null;
            }

            string repaired = await _derivation.RepairWebPForDisplayAsync(message.ImageUri);
            if (!string.IsNullOrWhiteSpace(repaired) &&
                !string.Equals(message.ImageUri, repaired, StringComparison.OrdinalIgnoreCase))
            {
                message.ImageUri = repaired;
                try
                {
                    await RememberAsync(message);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Persist ImageUri failed: " + ex.Message);
                }
            }

            return repaired;
        }

        private async Task<string> EnsurePlayableAudioUriAsync(ChatMessage message, string sourceUri)
        {
            if (message == null || string.IsNullOrWhiteSpace(sourceUri))
            {
                return sourceUri;
            }

            PlayableAudioFile playable = await _derivation.EnsurePlayableAsync(
                message.AudioMimeType,
                sourceUri,
                message.Id);
            if (playable == null ||
                string.IsNullOrWhiteSpace(playable.Uri) ||
                string.Equals(playable.Uri, sourceUri, StringComparison.Ordinal))
            {
                return sourceUri;
            }

            message.AudioUri = playable.Uri;
            message.AudioMimeType = playable.MimeType;
            try
            {
                await RememberAsync(message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Persist playable uri failed: " + ex.Message);
            }

            return playable.Uri;
        }

        private async Task<string> EnsureVideoPosterAsync(ChatMessage message)
        {
            if (!string.IsNullOrWhiteSpace(message.VideoPosterUri))
            {
                return message.VideoUri;
            }

            try
            {
                message.VideoPosterUri = await _derivation.TryCreateVideoPosterAsync(message.VideoUri, message.Id);
                if (!string.IsNullOrWhiteSpace(message.VideoPosterUri))
                {
                    await RememberAsync(message);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Video poster failed: " + ex.Message);
            }

            return message.VideoUri;
        }

        private async Task<string> SaveImageBytesForDisplayAsync(byte[] imageBytes, string fileBase, string mimeType)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                return null;
            }

            string effectiveMime = mimeType;
            bool isWebP = WebPHelpers.IsWebPMime(effectiveMime) || WebPHelpers.IsWebPPayload(imageBytes);
            if (isWebP && !WebPHelpers.IsWebPMime(effectiveMime))
            {
                effectiveMime = "image/webp";
            }

            string originalUri = await _cache.SaveAsync(
                MediaCacheKind.Image,
                fileBase,
                MediaFileExtensions.ForImage(effectiveMime ?? "image/jpeg"),
                imageBytes);
            if (string.IsNullOrWhiteSpace(originalUri) || !isWebP || WebPHelpers.HasWebPCodec)
            {
                return originalUri;
            }

            return await _derivation.EnsurePngDisplayCopyAsync(imageBytes, fileBase) ?? originalUri;
        }

        private async Task<string> TryReuseDownloadedFileAsync(
            MediaCacheKind kind,
            MediaDownloadPlan plan,
            string extension)
        {
            if (plan.ExpectedSha256 == null || plan.ExpectedSha256.Length == 0)
            {
                return null;
            }

            try
            {
                return await _cache.TryGetUriAsync(kind, plan.FileBase, extension);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Cache lookup failed (" + kind + "): " + ex.Message);
                return null;
            }
        }

        private async Task TryFillDocumentFileLengthFromLocalAsync(ChatMessage message)
        {
            if (message == null ||
                message.DocumentFileLengthBytes > 0 ||
                string.IsNullOrWhiteSpace(message.DocumentUri))
            {
                return;
            }

            try
            {
                StorageFile file = null;
                string uri = message.DocumentUri.Trim();
                if (uri.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
                {
                    file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri));
                }
                else if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    file = await StorageFile.GetFileFromPathAsync(new Uri(uri).LocalPath);
                }
                else if (System.IO.Path.IsPathRooted(uri))
                {
                    file = await StorageFile.GetFileFromPathAsync(uri);
                }

                if (file == null)
                {
                    return;
                }

                var props = await file.GetBasicPropertiesAsync();
                if (props != null && props.Size > 0)
                {
                    message.DocumentFileLengthBytes = props.Size > long.MaxValue
                        ? long.MaxValue
                        : (long)props.Size;

                    string chatJid = _jids.GetCanonicalJid(message.RemoteJid);
                    if (!string.IsNullOrWhiteSpace(chatJid))
                    {
                        await _host.SaveMessageQuietAsync(chatJid, message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Document size fill failed: " + ex.Message);
            }
        }
    }
}
