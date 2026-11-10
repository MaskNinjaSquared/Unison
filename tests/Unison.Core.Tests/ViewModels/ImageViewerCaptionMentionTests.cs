using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    /// <summary>
    /// The full-screen viewer caption renders through the same rich path as the bubble
    /// (emoji font + mentions + links), so it must surface the bubble's mention state
    /// rather than keeping a parallel copy of it.
    /// </summary>
    public class ImageViewerCaptionMentionTests
    {
        [Fact]
        public void Caption_mentions_come_from_the_bubble_view_model()
        {
            var message = new ChatMessageViewModel(new ChatMessage
            {
                Caption = "@16158070571 look at this \U0001FAE5",
                MentionedJids = new List<string> { "16158070571@s.whatsapp.net" }
            });

            ImageViewerViewModel viewer = CreateViewer(message);

            Assert.Equal(message.Caption, viewer.Caption);
            Assert.Same(message.MentionedJids, viewer.MentionedJids);
        }

        [Fact]
        public void Caption_mention_lookup_tracks_the_bubble_roster_flush()
        {
            var message = new ChatMessageViewModel(new ChatMessage
            {
                Caption = "@16158070571 look at this",
                MentionedJids = new List<string> { "16158070571@s.whatsapp.net" }
            });

            ImageViewerViewModel viewer = CreateViewer(message);
            Assert.Null(viewer.MentionLookup);

            int before = viewer.MentionRefreshKey;
            var lookup = new Dictionary<string, string> { ["16158070571"] = "Lindsay" };
            message.RefreshMentions(lookup);

            Assert.Same(lookup, viewer.MentionLookup);
            Assert.NotEqual(before, viewer.MentionRefreshKey);
        }

        [Fact]
        public void Missing_mentions_do_not_fabricate_an_empty_lookup()
        {
            var message = new ChatMessageViewModel(new ChatMessage { Caption = "no mentions here" });

            ImageViewerViewModel viewer = CreateViewer(message);

            Assert.Null(viewer.MentionLookup);
            Assert.Empty(viewer.MentionedJids);
        }

        private static ImageViewerViewModel CreateViewer(ChatMessageViewModel message) =>
            new ImageViewerViewModel(message, new NoopShareService(), new NoopFilePicker(), null);

        private sealed class NoopShareService : IShareService
        {
            public Task ShareLocalFileAsync(string title, string localUriOrPath) =>
                Task.CompletedTask;
        }
        private sealed class NoopFilePicker : IFilePicker
        {
            public Task<string> PickOpenImagePathAsync() => Task.FromResult<string>(null);

            public Task<string> PickSaveTextFileAsync(string suggestedFileName, string content) =>
                Task.FromResult<string>(null);

            public Task<PickedChatMedia> PickChatAttachmentAsync() =>
                Task.FromResult<PickedChatMedia>(null);

            public Task<PickedChatMedia> PickChatImageAsync() =>
                Task.FromResult<PickedChatMedia>(null);

            public Task<IReadOnlyList<PickedChatMedia>> PickChatImagesAsync() =>
                Task.FromResult<IReadOnlyList<PickedChatMedia>>(Array.Empty<PickedChatMedia>());

            public Task<PickedChatMedia> PickChatAudioAsync() =>
                Task.FromResult<PickedChatMedia>(null);

            public Task<PickedChatMedia> PickChatDocumentAsync() =>
                Task.FromResult<PickedChatMedia>(null);

            public Task<string> PickSaveLocalImageAsync(string sourceUriOrPath, string suggestedFileName) =>
                Task.FromResult<string>(null);

            public Task<string> PickSaveLocalFileAsync(
                string sourceUriOrPath,
                string suggestedFileName,
                string mimeType = null) => Task.FromResult<string>(null);
        }
    }
}
