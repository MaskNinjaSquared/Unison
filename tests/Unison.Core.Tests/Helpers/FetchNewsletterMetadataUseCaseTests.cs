using Unison.Baileys.Protocol;
using Unison.Socket.UseCases.Newsletter;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class FetchNewsletterMetadataUseCaseTests
    {
        [Fact]
        public void BuildQuery_is_w_mex_with_metadata_query_id()
        {
            BinaryNode iq = FetchNewsletterMetadataUseCase.BuildQuery(
                "120363@newsletter",
                "jid");

            Assert.Equal("iq", iq.Tag);
            Assert.Equal("w:mex", iq.GetAttribute("xmlns"));
            Assert.Equal("get", iq.GetAttribute("type"));

            BinaryNode query = iq.GetChild("query");
            Assert.Equal(FetchNewsletterMetadataUseCase.QueryId, query.GetAttribute("query_id"));

            string body = query.GetContentString();
            Assert.Contains("\"type\":\"JID\"", body);
            Assert.Contains("120363@newsletter", body);
            Assert.Contains("fetch_full_image", body);
        }

        [Fact]
        public void BuildQuery_escapes_quotes_in_key()
        {
            BinaryNode iq = FetchNewsletterMetadataUseCase.BuildQuery("a\"b", "invite");
            string body = iq.GetChild("query").GetContentString();
            Assert.Contains("\\\"", body);
            Assert.Contains("\"type\":\"INVITE\"", body);
        }
    }
}
