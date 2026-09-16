// =============================================================================
// Tests for MediaPreviewTag.
//
// These markers are closer to a wire format than to display text: the
// background task matches them to pick an icon and a localized label, and
// HistoryMessageMapper strips them when reading old rows. So the exact spelling
// is asserted here on purpose -- it is a contract with two other components,
// not a cosmetic string, and changing it silently stops it being recognised.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MediaPreviewTagTests
    {
        // --- Audio, which is where the two copies of this rule disagreed ---------

        [Fact]
        public void A_voice_note_and_an_audio_file_are_announced_differently()
        {
            // The distinction is the protocol's ptt flag, not the file type. A shared music
            // file described as "Voice message" misrepresents what arrived.
            Assert.Equal("[Voice Message]", MediaPreviewTag.ForAudio(isVoiceNote: true));
            Assert.Equal("[Audio]", MediaPreviewTag.ForAudio(isVoiceNote: false));
        }

        [Fact]
        public void The_chat_list_kind_deliberately_does_not_separate_the_two()
        {
            // Worth knowing before "fixing" it. ChatPreviewKind has no Audio member: both
            // land on Voice, because that enum drives the list icon and template, where the
            // two are shown the same way. The distinction is not lost -- it is carried
            // alongside, in IsVoiceNote, which HistoryMessageMapper reads to recover the
            // real kind.
            //
            // So a preview built from a chat row cannot tell them apart by kind alone, and
            // ChatPreviewMessageFactory's Audio branch is unreachable through it. That is
            // the design, not an oversight.
            Assert.Equal(ChatPreviewKind.Voice, ChatPreviewNormalizer.ToPreviewKind(ChatMessageKind.Audio));
            Assert.Equal(ChatPreviewKind.Voice, ChatPreviewNormalizer.ToPreviewKind(ChatMessageKind.Voice));
        }

        [Fact]
        public void Either_marker_is_read_back_as_an_audio_preview()
        {
            // The round trip that matters: whichever marker was written, recognising it
            // again yields the same list kind.
            Assert.Equal(ChatPreviewKind.Voice, ChatPreviewNormalizer.InferKindFromLegacyMediaTags(MediaPreviewTag.Voice));
            Assert.Equal(ChatPreviewKind.Voice, ChatPreviewNormalizer.InferKindFromLegacyMediaTags(MediaPreviewTag.Audio));
        }

        // --- Captions -------------------------------------------------------------

        [Fact]
        public void A_caption_follows_the_marker()
        {
            Assert.Equal("[Image] on the beach", MediaPreviewTag.ForImage("on the beach"));
            Assert.Equal("[Video] the whole trip", MediaPreviewTag.ForVideo("the whole trip"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void No_caption_means_the_bare_marker_with_nothing_trailing(string? caption)
        {
            // A trailing space would survive into the chat list and into notification text.
            Assert.Equal("[Image]", MediaPreviewTag.ForImage(caption));
            Assert.Equal("[Video]", MediaPreviewTag.ForVideo(caption));
            Assert.Equal("[Document]", MediaPreviewTag.ForDocument(caption));
        }

        [Fact]
        public void A_document_is_shown_by_its_file_name()
        {
            // There is no caption on a document, so the name is the only informative part.
            Assert.Equal("[Document] contract.pdf", MediaPreviewTag.ForDocument("contract.pdf"));
        }

        // --- The spellings the background task matches on ----------------------------

        [Fact]
        public void The_markers_keep_the_exact_spelling_the_notification_layer_looks_for()
        {
            // Unison.Background cannot reference Unison.Core, so it holds its own copy of
            // these strings and matches on them to choose an icon. If one side is reworded
            // the tag stops being recognised and the raw marker reaches the user as text.
            Assert.Equal("[Image]", MediaPreviewTag.Image);
            Assert.Equal("[Video]", MediaPreviewTag.Video);
            Assert.Equal("[Sticker]", MediaPreviewTag.Sticker);
            Assert.Equal("[Document]", MediaPreviewTag.Document);
            Assert.Equal("[Voice Message]", MediaPreviewTag.Voice);
            Assert.Equal("[Audio]", MediaPreviewTag.Audio);
        }
    }
}
