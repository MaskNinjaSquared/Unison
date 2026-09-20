using Proto;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingMediaHydrationTests
    {
        [Fact]
        public void A_sticker_and_an_image_are_both_planned()
        {
            var info = new MessageRenderInfo
            {
                IsSticker = true,
                StickerMessage = new Message.Types.StickerMessage { Mimetype = "image/webp" },
                IsImage = true,
                ImageMessage = new Message.Types.ImageMessage { Mimetype = "image/jpeg" }
            };

            IncomingMediaHydrationPlan plan = IncomingMediaHydrationPlan.From(info);

            Assert.True(plan.HasSticker);
            Assert.True(plan.HasImage);
            Assert.Same(info.StickerMessage, plan.Sticker);
            Assert.Same(info.ImageMessage, plan.Image);
        }

        [Fact]
        public void A_flag_without_the_sub_message_is_not_planned()
        {
            var info = new MessageRenderInfo { IsImage = true, ImageMessage = null };

            IncomingMediaHydrationPlan plan = IncomingMediaHydrationPlan.From(info);

            Assert.False(plan.HasImage);
            Assert.False(plan.HasAny);
        }

        [Fact]
        public void A_null_render_plans_nothing()
        {
            Assert.False(IncomingMediaHydrationPlan.From(null).HasAny);
        }
    }
}
