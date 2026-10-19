using Unison.Core.Models;

namespace Unison.Uwp.Client
{
    /// <summary>
    /// The short body a quote carries. The original protobuf is not kept, so the
    /// other phone learns the type and the caption from this stub.
    /// </summary>
    internal static class QuotedMessageStub
    {
        public static global::Proto.Message Create(OutgoingQuote quote)
        {
            var message = new global::Proto.Message();
            string text = quote != null ? quote.Text ?? string.Empty : string.Empty;
            ChatPreviewKind kind = quote != null ? quote.Kind : ChatPreviewKind.Text;
            switch (kind)
            {
                case ChatPreviewKind.Image:
                    message.ImageMessage = new global::Proto.Message.Types.ImageMessage { Caption = text };
                    break;
                case ChatPreviewKind.Video:
                    message.VideoMessage = new global::Proto.Message.Types.VideoMessage { Caption = text };
                    break;
                case ChatPreviewKind.Sticker:
                    message.StickerMessage = new global::Proto.Message.Types.StickerMessage();
                    break;
                case ChatPreviewKind.Voice:
                    message.AudioMessage = new global::Proto.Message.Types.AudioMessage { Ptt = true };
                    break;
                case ChatPreviewKind.Document:
                    message.DocumentMessage = new global::Proto.Message.Types.DocumentMessage
                    {
                        FileName = text,
                        Caption = text
                    };
                    break;
                default:
                    message.Conversation = text;
                    break;
            }

            return message;
        }
    }
}
