// =============================================================================
// IncomingMediaHydration
//
// Which media arms of an incoming MessageRenderInfo should start downloading
// beside the pump. Offline and live used to spell the same two ifs twice in
// different order; one plan keeps them aligned.
// =============================================================================
using Proto;

namespace Unison.Core.Helpers
{
    public readonly struct IncomingMediaHydrationPlan
    {
        public IncomingMediaHydrationPlan(
            Message.Types.StickerMessage sticker,
            Message.Types.ImageMessage image)
        {
            Sticker = sticker;
            Image = image;
        }

        public Message.Types.StickerMessage Sticker { get; }
        public Message.Types.ImageMessage Image { get; }

        public bool HasSticker => Sticker != null;
        public bool HasImage => Image != null;
        public bool HasAny => HasSticker || HasImage;

        public static IncomingMediaHydrationPlan From(MessageRenderInfo renderInfo)
        {
            if (renderInfo == null)
            {
                return default(IncomingMediaHydrationPlan);
            }

            Message.Types.StickerMessage sticker = renderInfo.IsSticker
                ? renderInfo.StickerMessage
                : null;
            Message.Types.ImageMessage image = renderInfo.IsImage
                ? renderInfo.ImageMessage
                : null;

            return new IncomingMediaHydrationPlan(sticker, image);
        }
    }
}
