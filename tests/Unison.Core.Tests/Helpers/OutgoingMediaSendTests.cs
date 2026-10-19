using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OutgoingMediaSendTests
    {
        private static readonly DateTime At = new DateTime(2026, 10, 12, 9, 30, 0, DateTimeKind.Utc);
        private static readonly byte[] Bytes = { 1, 2, 3 };

        private readonly FakeHost _host = new FakeHost();
        private readonly FakeJids _jids = new FakeJids();
        private readonly FakeCache _cache = new FakeCache();

        private OutgoingMediaSend Create(string id = "IMG1") =>
            new OutgoingMediaSend(_host, _jids, _cache, () => At, () => id);

        [Fact]
        public async Task Empty_image_is_rejected_without_reconnecting()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Create().SendImageAsync("555@s.whatsapp.net", new byte[0], null));

            Assert.Empty(_host.Log);
        }

        [Fact]
        public async Task Image_bubble_shows_as_pending_before_the_upload_then_settles_as_sent()
        {
            ChatMessage m = await Create().SendImageAsync("555@s.whatsapp.net", Bytes, "look");

            Assert.Equal(
                new[]
                {
                    "accept:555@s.whatsapp.net:pending",
                    "preview:555@s.whatsapp.net:Image",
                    "changed:555@s.whatsapp.net",
                    "persist:555@s.whatsapp.net:pending",
                    "connect",
                    "send-image:555@s.whatsapp.net:IMG1",
                    "persist:555@s.whatsapp.net:sent",
                    "queue-persist:555@s.whatsapp.net",
                    "changed:555@s.whatsapp.net"
                },
                _host.Log);
            Assert.Equal("IMG1", m.Id);
            Assert.Equal(ChatMessage.StatusSent, m.Status);
            Assert.Equal(ChatMessageKind.Image, m.Kind);
            Assert.Equal("look", m.Caption);
            Assert.Equal("cache://Image/IMG1_out" + MediaFileExtensions.ForImage("image/jpeg"), m.ImageUri);
            Assert.True(_cache.Saves[0].ReuseExisting);
            Assert.Same(Bytes, _cache.Saves[0].Bytes);
            Assert.Equal(At, m.Timestamp);
            Assert.Equal("111@s.whatsapp.net", m.ParticipantJid);
        }

        [Fact]
        public async Task A_refused_image_stays_on_screen_as_failed_instead_of_throwing()
        {
            var error = new InvalidOperationException("upload failed");
            _host.SendError = error;

            ChatMessage m = await Create().SendImageAsync("555@s.whatsapp.net", Bytes, null);

            Assert.Equal(ChatMessage.StatusFailed, m.Status);
            Assert.Contains("persist:555@s.whatsapp.net:failed", _host.Log);
            Assert.Contains("queue-persist:555@s.whatsapp.net", _host.Log);
            Assert.Same(error, _host.ReportedError);
            Assert.Single(_cache.Saves);
        }

        [Fact]
        public async Task An_image_that_cannot_reconnect_also_fails_in_place()
        {
            _host.ConnectError = new TimeoutException("offline");

            ChatMessage m = await Create().SendImageAsync("555@s.whatsapp.net", Bytes, null);

            Assert.Equal(ChatMessage.StatusFailed, m.Status);
            Assert.DoesNotContain(_host.Log, e => e.StartsWith("send-image:", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_status_the_server_already_reported_is_not_overwritten_by_sent()
        {
            _host.OnSend = message => message.Status = ChatMessage.StatusDelivered;

            ChatMessage m = await Create().SendImageAsync("555@s.whatsapp.net", Bytes, null);

            Assert.Equal(ChatMessage.StatusDelivered, m.Status);
        }

        [Fact]
        public async Task Image_is_filed_under_the_normalized_jid_not_the_canonical_one()
        {
            _jids.Canonical["999@lid"] = "555@s.whatsapp.net";

            ChatMessage m = await Create().SendImageAsync("999@lid", Bytes, null);

            Assert.Equal("999@lid", m.RemoteJid);
        }

        [Fact]
        public async Task Image_in_a_self_chat_is_read()
        {
            _jids.SelfLinked.Add("111@s.whatsapp.net");

            ChatMessage m = await Create().SendImageAsync("111@s.whatsapp.net", Bytes, null);

            Assert.Equal(ChatMessage.StatusRead, m.Status);
        }

        [Fact]
        public async Task Empty_audio_is_rejected_without_reconnecting()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Create().SendAudioAsync("555@s.whatsapp.net", Audio(new byte[0], true)));

            Assert.Empty(_host.Log);
        }

        [Fact]
        public async Task Voice_note_lands_in_the_canonical_chat_cached_fresh_and_prepared_for_playback()
        {
            _jids.Canonical["999@lid"] = "555@s.whatsapp.net";

            ChatMessage m = await Create("AUD1").SendAudioAsync("999@lid", Audio(Bytes, true));

            Assert.Equal(
                new[]
                {
                    "accept:555@s.whatsapp.net:pending",
                    "preview:555@s.whatsapp.net:Voice",
                    "changed:555@s.whatsapp.net",
                    "persist:555@s.whatsapp.net:pending",
                    "connect",
                    "send-audio:999@lid:AUD1",
                    "persist:555@s.whatsapp.net:sent",
                    "queue-persist:555@s.whatsapp.net",
                    "changed:555@s.whatsapp.net"
                },
                _host.Log);
            Assert.Equal("555@s.whatsapp.net", m.RemoteJid);
            Assert.Equal(ChatMessageKind.Voice, m.Kind);
            Assert.Equal(9u, m.AudioDurationSeconds);
            Assert.Equal("cache://Audio/AUD1_out" + MediaFileExtensions.ForAudio("audio/ogg; codecs=opus"), m.AudioUri);
            Assert.False(_cache.Saves[0].ReuseExisting);
        }

        [Fact]
        public async Task Plain_audio_still_previews_as_voice()
        {
            ChatMessage m = await Create("AUD1").SendAudioAsync("555@s.whatsapp.net", Audio(Bytes, false));

            Assert.Equal(ChatMessageKind.Audio, m.Kind);
            Assert.Contains("preview:555@s.whatsapp.net:Voice", _host.Log);
        }

        [Fact]
        public async Task A_refused_voice_note_stays_on_screen_as_failed()
        {
            _host.SendError = new InvalidOperationException("upload failed");

            ChatMessage m = await Create("AUD1").SendAudioAsync("555@s.whatsapp.net", Audio(Bytes, true));

            Assert.Equal(ChatMessage.StatusFailed, m.Status);
            Assert.Equal("cache://Audio/AUD1_out" + MediaFileExtensions.ForAudio("audio/ogg; codecs=opus"), m.AudioUri);
        }

        [Fact]
        public async Task Empty_document_is_rejected_without_reconnecting()
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => Create().SendDocumentAsync("555@s.whatsapp.net", Document(new byte[0])));

            Assert.Empty(_host.Log);
        }

        [Fact]
        public async Task Image_mime_as_document_is_rejected_without_reconnecting()
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => Create().SendDocumentAsync(
                    "555@s.whatsapp.net",
                    Document(Bytes, "photo.jpg", "image/jpeg")));

            Assert.Empty(_host.Log);
        }

        [Fact]
        public async Task Document_lands_in_the_canonical_chat_cached_fresh_then_settles_as_sent()
        {
            _jids.Canonical["999@lid"] = "555@s.whatsapp.net";

            ChatMessage m = await Create("DOC1").SendDocumentAsync("999@lid", Document(Bytes));

            Assert.Equal(
                new[]
                {
                    "accept:555@s.whatsapp.net:pending",
                    "preview:555@s.whatsapp.net:Document",
                    "changed:555@s.whatsapp.net",
                    "persist:555@s.whatsapp.net:pending",
                    "connect",
                    "send-document:999@lid:DOC1",
                    "persist:555@s.whatsapp.net:sent",
                    "queue-persist:555@s.whatsapp.net",
                    "changed:555@s.whatsapp.net"
                },
                _host.Log);
            Assert.Equal("555@s.whatsapp.net", m.RemoteJid);
            Assert.Equal(ChatMessageKind.Document, m.Kind);
            Assert.Equal("contract.pdf", m.DocumentFileName);
            Assert.Equal("application/pdf", m.DocumentMimeType);
            Assert.Equal(3L, m.DocumentFileLengthBytes);
            Assert.Equal(
                "cache://Document/DOC1_out" + MediaFileExtensions.ForDocument("contract.pdf", "application/pdf"),
                m.DocumentUri);
            Assert.False(_cache.Saves[0].ReuseExisting);
        }

        [Fact]
        public async Task A_refused_document_stays_on_screen_as_failed()
        {
            _host.SendError = new InvalidOperationException("upload failed");

            ChatMessage m = await Create("DOC1").SendDocumentAsync("555@s.whatsapp.net", Document(Bytes));

            Assert.Equal(ChatMessage.StatusFailed, m.Status);
            Assert.Contains("persist:555@s.whatsapp.net:failed", _host.Log);
            Assert.Same(_host.SendError, _host.ReportedError);
        }

        private static OutgoingAudio Audio(byte[] bytes, bool isVoice) => new OutgoingAudio
        {
            Bytes = bytes,
            MimeType = "audio/ogg; codecs=opus",
            DurationSeconds = 9,
            IsVoiceMessage = isVoice
        };

        private static OutgoingDocument Document(
            byte[] bytes,
            string fileName = "contract.pdf",
            string mime = "application/pdf") => new OutgoingDocument
        {
            Bytes = bytes,
            FileName = fileName,
            MimeType = mime
        };

        private sealed class FakeHost : IOutgoingMessageHost
        {
            public List<string> Log { get; } = new List<string>();
            public Exception SendError { get; set; }
            public Exception ConnectError { get; set; }
            public Exception ReportedError { get; private set; }

            /// <summary>Stands in for a receipt landing on the bubble while the upload is out.</summary>
            public Action<ChatMessage> OnSend { get; set; }

            private ChatMessage _accepted;

            public Task EnsureConnectedAsync()
            {
                Log.Add("connect");
                if (ConnectError != null)
                {
                    throw ConnectError;
                }

                return Task.CompletedTask;
            }

            public Task SendImageAsync(string jid, byte[] imageBytes, string caption, string messageId)
            {
                Log.Add("send-image:" + jid + ":" + messageId);
                return Sent();
            }

            public Task SendAudioAsync(string jid, OutgoingAudio audio, string messageId)
            {
                Log.Add("send-audio:" + jid + ":" + messageId);
                return Sent();
            }

            public Task SendDocumentAsync(string jid, OutgoingDocument document, string messageId)
            {
                Log.Add("send-document:" + jid + ":" + messageId);
                return Sent();
            }

            private Task Sent()
            {
                if (SendError != null)
                {
                    throw SendError;
                }

                OnSend?.Invoke(_accepted);
                return Task.CompletedTask;
            }

            public void AcceptOutgoing(string chatJid, ChatMessage message)
            {
                _accepted = message;
                Log.Add("accept:" + chatJid + ":" + message.Status);
            }

            public Task ShowLocalSendPreviewAsync(string chatJid, ChatMessage message, ChatPreviewKind kind)
            {
                Log.Add("preview:" + chatJid + ":" + kind);
                return Task.CompletedTask;
            }

            public void NotifyChatChanged(string chatJid) => Log.Add("changed:" + chatJid);

            public void QueueForPersist(string chatJid, ChatMessage message) => Log.Add("queue-persist:" + chatJid);

            public Task PersistLiveAsync(string chatJid, ChatMessage message)
            {
                Log.Add("persist:" + chatJid + ":" + message.Status);
                return Task.CompletedTask;
            }

            public Task<OutgoingSendAttempt> TrySendTextOnceAsync(OutgoingTextAttempt request) => throw new NotSupportedException();

            public void InvalidateTransport(OutgoingSendAttempt attempt, string reason) => throw new NotSupportedException();

            public void RecordSendAttempt(string messageId) => throw new NotSupportedException();

            public void RecordSendSuccess(string messageId) => throw new NotSupportedException();

            public void RecordSendFailure(string messageId, Exception error) => throw new NotSupportedException();

            public void ReportError(Exception error) => ReportedError = error;

            public Task<List<ChatMessage>> LoadLiveOverlayAsync(string chatJid) =>
                throw new NotSupportedException();
        }

        private sealed class FakeCache : IMediaCache
        {
            public List<(MediaCacheKind Kind, string FileBase, byte[] Bytes, bool ReuseExisting)> Saves { get; } =
                new List<(MediaCacheKind, string, byte[], bool)>();

            public Task<string> SaveAsync(
                MediaCacheKind kind,
                string fileBase,
                string extension,
                byte[] bytes,
                bool reuseExisting = true,
                CancellationToken token = default(CancellationToken))
            {
                Saves.Add((kind, fileBase, bytes, reuseExisting));
                return Task.FromResult("cache://" + kind + "/" + fileBase + extension);
            }

            public Task<string> TryGetUriAsync(MediaCacheKind kind, string fileBase, string extension) => Task.FromResult<string>(null);

            public Task<byte[]> TryReadAsync(string cacheUri) => Task.FromResult<byte[]>(null);

            public string SanitizeFileBase(string fileBase) => fileBase;
        }

        private sealed class FakeJids : IJidResolver
        {
            public Dictionary<string, string> Canonical { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> SelfLinked { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Profile Self { get; } = new Profile { Id = "111@s.whatsapp.net" };

            public string GetCanonicalJid(string jid)
            {
                string value;
                return jid != null && Canonical.TryGetValue(jid, out value) ? value : jid;
            }

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => jid != null && SelfLinked.Contains(jid);
        }
    }
}
