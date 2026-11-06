// =============================================================================
// Channel create / picture update as WMex IQs (Baileys newsletterCreate and
// newsletterUpdatePicture in src/Socket/newsletter.ts).
// =============================================================================
using Newtonsoft.Json.Linq;
using Unison.Baileys.Protocol;
using Unison.Socket.UseCases.Newsletter;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class NewsletterCreateQueryTests
    {
        [Fact]
        public void Create_is_a_w_mex_get_with_the_create_query_id()
        {
            BinaryNode iq = CreateNewsletterUseCase.BuildQuery("Unison \"News\"", "Updates");

            Assert.Equal("iq", iq.Tag);
            Assert.Equal("w:mex", iq.GetAttribute("xmlns"));
            Assert.Equal("get", iq.GetAttribute("type"));
            BinaryNode query = iq.GetChild("query");
            Assert.Equal("8823471724422422", query.GetAttribute("query_id"));

            JObject input = (JObject)JObject.Parse(query.GetContentString())["variables"]["input"];
            Assert.Equal("Unison \"News\"", (string)input["name"]);
            Assert.Equal("Updates", (string)input["description"]);
        }

        [Fact]
        public void Create_without_description_sends_null()
        {
            BinaryNode iq = CreateNewsletterUseCase.BuildQuery("News", "  ");

            JObject input = (JObject)JObject.Parse(iq.GetChild("query").GetContentString())["variables"]["input"];
            Assert.Equal(JTokenType.Null, input["description"].Type);
        }

        [Fact]
        public void Picture_update_targets_the_channel_with_base64_jpeg()
        {
            BinaryNode iq = UpdateNewsletterPictureUseCase.BuildQuery(
                "120363000000000010@newsletter",
                new byte[] { 0xFF, 0xD8, 0xFF });

            BinaryNode query = iq.GetChild("query");
            Assert.Equal("24250201037901610", query.GetAttribute("query_id"));

            JObject variables = (JObject)JObject.Parse(query.GetContentString())["variables"];
            Assert.Equal("120363000000000010@newsletter", (string)variables["newsletter_id"]);
            Assert.Equal("/9j/", (string)variables["updates"]["picture"]);
            Assert.Equal(JTokenType.Null, variables["updates"]["settings"].Type);
        }
    }
}
