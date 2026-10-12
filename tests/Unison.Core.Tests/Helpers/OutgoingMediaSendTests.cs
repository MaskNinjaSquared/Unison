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

        private OutgoingMediaSend Create() => new OutgoingMediaSend(_host, _jids, _cache, () => At);

        [Fact]
        public async Task Empty_image_is_rejected_without_reconnecting()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Create().SendImageAsync("555@s.whatsapp.net", new byte[0], null));

            Assert.Empty(_host.Log);
        }

        [Fact]
        public async Task Image_is_sent_first_then_cached_under_its_id_and_shown_as_sent()
        {
            ChatMessage m = await Create().SendImageAsync("555@s.whatsapp.net", Bytes, "look");

            Assert.Equal(
                new[]
                {
                    "connect",
                    "send-image:555@s.whatsapp.net",
                    "accept:555@s.whatsapp.net:sent",
                    "preview:555@s.whatsapp.net:Image",
                    "queue-persist:555@s.whatsapp.net",
                    "changed:555@s.whatsapp.net"
                },
                _host.Log);
            Assert.Equal("IMG1", m.Id);
            Assert.Equal(ChatMessageKind.Image, m.Kind);
            Assert.Equal("look", m.Caption);
            Assert.Equal("cache://Image/IMG1_out" + MediaFileExtensions.ForImage("image/jpeg"), m.ImageUri);
            Assert.True(_cache.Saves[0].ReuseExisting);
            Assert.Same(Bytes, _cache.Saves[0].Bytes);
            Assert.Equal(At, m.Timestamp);
            Assert.Equal("111@s.whatsapp.net", m.ParticipantJid);
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
        public async Task A_refused_image_throws_and_leaves_no_bubble()
        {
            _host.SendError = new InvalidOperationException("upload failed");

            await Assert.ThrowsAsync<InvalidOperationException>(() => Create().SendImageAsync("555@s.whatsapp.net", Bytes, null));

            Assert.DoesNotContain(_host.Log, e => e.StartsWith("accept:", StringComparison.Ordinal));
            Assert.Empty(_cache.Saves);
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

            ChatMessage m = await Create().SendAudioAsync("999@lid", Audio(Bytes, true));

            Assert.Equal(
                new[]
                {
                    "connect",
                    "send-audio:999@lid",
                    "accept:555@s.whatsapp.net:sent",
                    "preview:555@s.whatsapp.net:Voice",
                    "queue-persist:555@s.whatsapp.net",
                    "changed:555@s.whatsapp.net",
                    "prepare-playback:AUD1"
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
            ChatMessage m = await Create().SendAudioAsync("555@s.whatsapp.net", Audio(Bytes, false));

            Assert.Equal(ChatMessageKind.Audio, m.Kind);
            Assert.Contains("preview:555@s.whatsapp.net:Voice", _host.Log);
        }

        private static OutgoingAudio Audio(byte[] bytes, bool isVoice) => new OutgoingAudio
        {
            Bytes = bytes,
            MimeType = "audio/ogg; codecs=opus",
            DurationSeconds = 9,
            IsVoiceMessage = isVoice
        };

        private sealed class FakeHost : IOutgoingMessageHost
        {
            public List<string> Log { get; } = new List<string>();
            public Exception SendError { get; set; }

            public Task EnsureConnectedAsync()
            {
                Log.Add("connect");
                return Task.CompletedTask;
            }

            public Task<string> SendImageAsync(string jid, byte[] imageBytes, string caption)
            {
                Log.Add("send-image:" + jid);
                if (SendError != null)
                {
                    throw SendError;
                }

                return Task.FromResult("IMG1");
            }

            public Task<string> SendAudioAsync(string jid, OutgoingAudio audio)
            {
                Log.Add("send-audio:" + jid);
                return Task.FromResult("AUD1");
            }

            public void PrepareLocalPlayback(ChatMessage message) => Log.Add("prepare-playback:" + message.Id);

            public void AcceptOutgoing(string chatJid, ChatMessage message) =>
                Log.Add("accept:" + chatJid + ":" + message.Status);

            public Task ShowLocalSendPreviewAsync(string chatJid, ChatMessage message, ChatPreviewKind kind)
            {
                Log.Add("preview:" + chatJid + ":" + kind);
                return Task.CompletedTask;
            }

            public void NotifyChatChanged(string chatJid) => Log.Add("changed:" + chatJid);

            public void QueueForPersist(string chatJid, ChatMessage message) => Log.Add("queue-persist:" + chatJid);

            public Task PersistLiveAsync(string chatJid, ChatMessage message) => throw new NotSupportedException();

            public Task<OutgoingSendAttempt> TrySendTextOnceAsync(OutgoingTextAttempt request) => throw new NotSupportedException();

            public void InvalidateTransport(OutgoingSendAttempt attempt, string reason) => throw new NotSupportedException();

            public void RecordSendAttempt(string messageId) => throw new NotSupportedException();

            public void RecordSendSuccess(string messageId) => throw new NotSupportedException();

            public void RecordSendFailure(string messageId, Exception error) => throw new NotSupportedException();

            public void ReportError(Exception error) => throw new NotSupportedException();
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
