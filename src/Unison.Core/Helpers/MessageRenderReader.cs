using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// What an incoming envelope should put on screen: the preview line, which kind of media it is,
    /// and the media sub-message the download path needs.
    /// </summary>
    public sealed class MessageRenderInfo
    {
        public string Content { get; set; }
        public bool IsImage { get; set; }
        public bool IsVideo { get; set; }
        public bool IsSticker { get; set; }
        public string Caption { get; set; }
        public Proto.Message.Types.ImageMessage ImageMessage { get; set; }
        public Proto.Message.Types.StickerMessage StickerMessage { get; set; }
        public Proto.Message.Types.VideoMessage VideoMessage { get; set; }
        public bool IsAudio { get; set; }
        public bool IsVoice { get; set; }
        public bool IsDocument { get; set; }
        public Proto.Message.Types.DocumentMessage DocumentMessage { get; set; }
        public Proto.Message.Types.AudioMessage AudioMessage { get; set; }

        // Filled in by the caller from the envelope's context info, which needs name resolution.
        public string QuotedText { get; set; }
        public string QuotedSenderName { get; set; }
        public List<string> MentionedJids { get; set; }

        public ChatPreviewKind PreviewKind
        {
            get
            {
                if (IsSticker) return ChatPreviewKind.Sticker;
                if (IsImage) return ChatPreviewKind.Image;
                if (IsVideo) return ChatPreviewKind.Video;
                if (IsDocument) return ChatPreviewKind.Document;
                if (IsVoice || IsAudio) return ChatPreviewKind.Voice;
                return ChatPreviewKind.Text;
            }
        }
    }

    /// <summary>
    /// Reads an incoming envelope into <see cref="MessageRenderInfo"/>.
    /// </summary>
    /// <remarks>
    /// A cascade of "if this field is set, it is that kind of message", where the order of the arms
    /// is part of the meaning rather than an accident of writing: an envelope assembled field by
    /// field can arrive with more than one set, and the first match wins. Returning null means
    /// "this is not a row in the timeline" — a reaction or a revocation changes a message that is
    /// already there.
    /// </remarks>
    public static class MessageRenderReader
    {
        /// <param name="log">Optional diagnostics sink; the reader itself decides nothing by it.</param>
        public static MessageRenderInfo Read(Proto.Message msg, Action<string> log = null)
        {
            Proto.Message unwrapped = HistorySyncContentFilter.Unwrap(msg);
            if (unwrapped == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(unwrapped.Conversation))
            {
                return new MessageRenderInfo { Content = unwrapped.Conversation };
            }

            if (unwrapped.ExtendedTextMessage != null &&
                !string.IsNullOrEmpty(unwrapped.ExtendedTextMessage.Text))
            {
                return new MessageRenderInfo { Content = unwrapped.ExtendedTextMessage.Text };
            }

            // Sticker before image, and not by preference: a live envelope merged field by field can
            // leave both set, and the image in that case is the sticker's thumbnail.
            if (unwrapped.StickerMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = MediaPreviewTag.Sticker,
                    IsSticker = true,
                    StickerMessage = unwrapped.StickerMessage
                };
            }

            if (unwrapped.ImageMessage != null)
            {
                string caption = unwrapped.ImageMessage.Caption ?? string.Empty;
                return new MessageRenderInfo
                {
                    Content = MediaPreviewTag.ForImage(caption),
                    IsImage = true,
                    Caption = caption,
                    ImageMessage = unwrapped.ImageMessage
                };
            }

            if (unwrapped.VideoMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = MediaPreviewTag.ForVideo(unwrapped.VideoMessage.Caption),
                    IsVideo = true,
                    Caption = unwrapped.VideoMessage.Caption ?? string.Empty,
                    VideoMessage = unwrapped.VideoMessage
                };
            }

            if (unwrapped.DocumentMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = MediaPreviewTag.ForDocument(unwrapped.DocumentMessage.FileName),
                    IsDocument = true,
                    DocumentMessage = unwrapped.DocumentMessage
                };
            }

            if (unwrapped.AudioMessage != null)
            {
                bool isVoice = unwrapped.AudioMessage.Ptt == true;
                return new MessageRenderInfo
                {
                    Content = MediaPreviewTag.ForAudio(isVoice),
                    IsAudio = true,
                    IsVoice = isVoice,
                    AudioMessage = unwrapped.AudioMessage
                };
            }

            // Reaction envelopes are handled by IChatMessageMapper / IReactionMapper, not as rows.
            if (unwrapped.ReactionMessage != null)
            {
                return null;
            }

            if (unwrapped.PollCreationMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = "[Poll] " + unwrapped.PollCreationMessage.Name
                };
            }

            if (unwrapped.ProtocolMessage != null)
            {
                MessageRenderInfo protocolRow;
                if (TryReadProtocolMessage(unwrapped.ProtocolMessage, log, out protocolRow))
                {
                    return protocolRow;
                }
            }

            if (unwrapped.ContactMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = "[Contact] " + unwrapped.ContactMessage.DisplayName
                };
            }

            if (unwrapped.LocationMessage != null)
            {
                return new MessageRenderInfo { Content = "[Location]" };
            }

            if (unwrapped.CallLogMesssage != null)
            {
                string outcome = unwrapped.CallLogMesssage.CallOutcome.ToString();
                string duration = unwrapped.CallLogMesssage.DurationSecs > 0
                    ? " (" + unwrapped.CallLogMesssage.DurationSecs + "s)"
                    : string.Empty;
                return new MessageRenderInfo { Content = "[Call] " + outcome + duration };
            }

            if (unwrapped.ScheduledCallCreationMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = "[Scheduled Call] " + unwrapped.ScheduledCallCreationMessage.Title
                };
            }

            if (unwrapped.Call != null)
            {
                return new MessageRenderInfo { Content = "[Call]" };
            }

            if (log != null)
            {
                log("Unknown message type, no content extracted: " + DescribeSetFields(unwrapped));
            }

            return null;
        }

        /// <summary>
        /// True when the protocol message is accounted for, whether or not it renders.
        /// </summary>
        private static bool TryReadProtocolMessage(
            Proto.Message.Types.ProtocolMessage protocol,
            Action<string> log,
            out MessageRenderInfo info)
        {
            info = null;

            // The delete, applied as an update to the message it names.
            if ((int)protocol.Type == 0)
            {
                return true;
            }

            if (protocol.HistorySyncNotification != null)
            {
                return true;
            }

            if (protocol.PeerDataOperationRequestResponseMessage != null)
            {
                if (log != null)
                {
                    log(DescribePeerDataOperation(protocol.PeerDataOperationRequestResponseMessage));
                }

                return true;
            }

            return false;
        }

        private static string DescribePeerDataOperation(
            Proto.Message.Types.PeerDataOperationRequestResponseMessage response)
        {
            var result = response.PeerDataOperationResult == null
                ? null
                : response.PeerDataOperationResult.FirstOrDefault();

            string fullCode = result != null && result.FullHistorySyncOnDemandRequestResponse != null
                ? result.FullHistorySyncOnDemandRequestResponse.ResponseCode.ToString()
                : string.Empty;
            string chunkCode = result != null && result.HistorySyncChunkRetryResponse != null
                ? result.HistorySyncChunkRetryResponse.ResponseCode.ToString()
                : string.Empty;

            return "PeerDataOperationResponse message observed: type=" +
                   response.PeerDataOperationRequestType +
                   ", stanzaId=" + response.StanzaId +
                   ", fullHistoryCode=" + fullCode +
                   ", chunkRetryCode=" + chunkCode;
        }

        private static string DescribeSetFields(Proto.Message unwrapped)
        {
            IEnumerable<string> names = unwrapped
                .GetType()
                .GetProperties()
                .Where(p => p.PropertyType == typeof(object) || p.PropertyType.IsClass)
                .Where(p => p.GetValue(unwrapped) != null)
                .Select(p => p.Name);

            return string.Join(", ", names);
        }
    }
}
