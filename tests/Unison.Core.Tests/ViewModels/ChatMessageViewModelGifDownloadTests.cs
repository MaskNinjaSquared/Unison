using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Core.Tests.ViewModels
{
    public class ChatMessageViewModelGifDownloadTests
    {
        [Fact]
        public void Gif_without_local_file_needs_video_download()
        {
            var model = new ChatMessage
            {
                Kind = ChatMessageKind.Gif,
                VideoMediaKeyBase64 = "c2FtcGxlLW1lZGlhLWtleQ==",
                VideoDirectPath = "/v/t62/gif.bin"
            };

            Assert.True(model.IsVideoMedia);
            Assert.True(model.NeedsVideoDownload);
            Assert.True(model.CanDownloadVideo);
        }

        [Fact]
        public async Task EnsureVideoReady_calls_facade_for_gif()
        {
            var facade = new FakeMessages();
            var model = new ChatMessage
            {
                Kind = ChatMessageKind.Gif,
                VideoMediaKeyBase64 = "c2FtcGxlLW1lZGlhLWtleQ==",
                VideoDirectPath = "/v/t62/gif.bin"
            };
            // Low: no auto-download — this test only covers the explicit ensure path.
            var vm = new ChatMessageViewModel(
                model,
                messages: facade,
                deviceCategory: DeviceCategory.Low);

            string uri = await vm.EnsureVideoReadyAsync(showErrorDialog: false);

            Assert.Equal(1, facade.EnsureVideoCalls);
            Assert.Same(model, facade.LastMessage);
            Assert.Equal("ms-appdata:///local/MediaCache/Videos/gif.mp4", uri);
            Assert.Equal(uri, model.VideoUri);
        }

        [Fact]
        public async Task High_auto_downloads_gif_when_bubble_loads()
        {
            var facade = new FakeMessages();
            var model = new ChatMessage
            {
                Kind = ChatMessageKind.Gif,
                VideoMediaKeyBase64 = "c2FtcGxlLW1lZGlhLWtleQ==",
                VideoDirectPath = "/v/t62/gif.bin"
            };
            var vm = new ChatMessageViewModel(
                model,
                messages: facade,
                deviceCategory: DeviceCategory.High);

            Assert.False(vm.ShowVideoDownloadIcon);
            await vm.GifAutoDownloadCompletion;

            Assert.Equal(1, facade.EnsureVideoCalls);
            Assert.True(model.HasLocalVideo);
            Assert.False(vm.NeedsVideoDownload);
            Assert.False(vm.ShowVideoDownloadIcon);
            Assert.True(vm.ShowGifSurface);
        }

        [Fact]
        public async Task Low_does_not_auto_download_gif()
        {
            var facade = new FakeMessages();
            var model = new ChatMessage
            {
                Kind = ChatMessageKind.Gif,
                VideoMediaKeyBase64 = "c2FtcGxlLW1lZGlhLWtleQ==",
                VideoDirectPath = "/v/t62/gif.bin"
            };
            var vm = new ChatMessageViewModel(
                model,
                messages: facade,
                deviceCategory: DeviceCategory.Low);

            await vm.GifAutoDownloadCompletion;

            Assert.Equal(0, facade.EnsureVideoCalls);
            Assert.True(vm.ShowVideoDownloadIcon);
        }

        [Fact]
        public async Task High_shows_download_icon_after_gif_auto_download_fails()
        {
            var facade = new FakeMessages { FailEnsure = true };
            var model = new ChatMessage
            {
                Kind = ChatMessageKind.Gif,
                VideoMediaKeyBase64 = "c2FtcGxlLW1lZGlhLWtleQ==",
                VideoDirectPath = "/v/t62/gif.bin"
            };
            var vm = new ChatMessageViewModel(
                model,
                messages: facade,
                deviceCategory: DeviceCategory.High);

            await vm.GifAutoDownloadCompletion;

            Assert.True(vm.NeedsVideoDownload);
            Assert.True(vm.ShowVideoDownloadIcon);
        }

        private sealed class FakeMessages : IMessageService
        {
            public int EnsureVideoCalls { get; private set; }
            public ChatMessage LastMessage { get; private set; }
            public bool FailEnsure { get; set; }

            public event EventHandler<string> ChatMessagesChanged
            {
                add { }
                remove { }
            }

            public event EventHandler<PresenceUpdateEventArgs> PresenceUpdated
            {
                add { }
                remove { }
            }

            public Task<string> EnsureVideoAvailableAsync(ChatMessage message)
            {
                EnsureVideoCalls++;
                LastMessage = message;
                if (FailEnsure)
                {
                    throw new InvalidOperationException("gif unavailable");
                }

                message.VideoUri = "ms-appdata:///local/MediaCache/Videos/gif.mp4";
                return Task.FromResult(message.VideoUri);
            }

            public Task SubscribeToPresenceAsync(string jid) => throw new NotImplementedException();
            public Task SyncMessageHistoryAsync(HistorySync sync) => throw new NotImplementedException();
            public Task<List<ChatMessage>> LoadMessagesForChatAsync(string jid) => throw new NotImplementedException();
            public Task<List<ChatMessage>> LoadRecentMessagesForSyncAsync(string jid) => throw new NotImplementedException();
            public Task<List<ChatMessage>> LoadMoreMessagesAsync(string jid, DateTime? beforeUtc = null, string beforeMessageId = null) => throw new NotImplementedException();
            public Task<List<ChatMessage>> LoadChatMediaIndexAsync(string jid, int limit = 400) => throw new NotImplementedException();
            public bool IsChatInfoMedia(ChatMessage message) => false;
            public bool IsChatInfoDocument(ChatMessage message) => false;
            public void CopyReactionState(ChatMessage target, ChatMessage source) { }
            public ChatMessage TryCreatePreviewFallback(ChatItem chat, string selfDisplayName = null) => null;
            public bool IsPreviewFallbackMessageId(string id) => false;
            public Task<bool> EnsureHistoryOnDemandAsync(string jid, int count) => throw new NotImplementedException();
            public bool IsHistoryOnDemandPending(string jid) => false;
            public Task<ChatMessage> SendTextMessageAsync(string jid, string text) => throw new NotImplementedException();
            public Task<ChatMessage> SendTextMessageAsync(string jid, OutgoingTextMentions message) => throw new NotImplementedException();
            public Task SendImageAsync(string jid, byte[] imageBytes, string caption) => throw new NotImplementedException();
            public Task<IReadOnlyList<ChatMessage>> SendImagesAsync(
                string jid,
                IReadOnlyList<byte[]> imageBytes,
                string caption) => throw new NotImplementedException();
            public Task<ChatMessage> SendAudioMessageAsync(string jid, byte[] audioBytes, string mimeType, uint durationSeconds, bool isVoiceMessage = false) => throw new NotImplementedException();
            public Task<ChatMessage> SendDocumentAsync(string jid, byte[] documentBytes, string mimeType, string fileName, string caption = null) => throw new NotImplementedException();
            public Task<string> EnsureAudioAvailableAsync(ChatMessage message) => throw new NotImplementedException();
            public Task<string> EnsureImageAvailableAsync(ChatMessage message) => throw new NotImplementedException();
            public Task<string> EnsureDocumentAvailableAsync(ChatMessage message) => throw new NotImplementedException();
            public Task HydrateIncomingImageAsync(ChatMessage message, Message.Types.ImageMessage image, string messageId, string chatJid) => throw new NotImplementedException();
            public Task HydrateIncomingStickerAsync(ChatMessage message, Message.Types.StickerMessage sticker, string messageId, string chatJid) => throw new NotImplementedException();
            public Task SetMessagePinnedAsync(string chatJid, ChatMessage message, bool pin, uint durationSeconds = 604800) => throw new NotImplementedException();
            public bool TryHandleReaction(Message message, ChatMessageMapContext context, IList<ChatMessage> chatMessages, out ChatMessage updatedParent)
            {
                updatedParent = null;
                return false;
            }

            public bool TryBufferReaction(Message message, ChatMessageMapContext context, out PendingReaction pending)
            {
                pending = default;
                return false;
            }

            public ChatMessage GetChatMessage(ChatMessageMapContext context, ChatMessageContentSnapshot content) => throw new NotImplementedException();
            public void AttachHistoryReactions(ChatMessage parent, IEnumerable<Reaction> reactions, ChatMessageMapContext parentContext) { }
            public IList<ChatMessage> ApplyBufferedReactions(IList<ChatMessage> chatMessages, IEnumerable<PendingReaction> pending) => chatMessages;
            public Task ResyncConversationsAsync(IProgress<ConversationResyncPhase> progress = null) => throw new NotImplementedException();
            public void NotifyLiveIncoming(string chatJid, string chatName, string senderName, string preview, bool isGroup, bool isFromMe, bool suppressToast, int totalUnread, ChatItem chat) { }
        }
    }
}
