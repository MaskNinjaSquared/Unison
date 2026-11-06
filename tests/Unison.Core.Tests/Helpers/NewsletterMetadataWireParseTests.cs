using Unison.Socket.UseCases.Newsletter;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class NewsletterMetadataWireParseTests
    {
        [Fact]
        public void Parses_thread_metadata_picture_and_name()
        {
            const string json =
                "{" +
                "\"data\":{\"xwa2_newsletter\":{" +
                "\"id\":\"120363@newsletter\"," +
                "\"thread_metadata\":{" +
                "\"name\":{\"text\":\"Unison News\"}," +
                "\"picture\":{\"id\":\"pic-1\",\"direct_path\":\"/v/t61/abc.jpg\"}," +
                "\"preview\":{\"id\":\"prev-1\",\"direct_path\":\"/v/t61/preview.jpg\"}" +
                "}}}}";

            NewsletterMetadataResult result = NewsletterMetadataWireParse.TryParse(json);

            Assert.Equal("120363@newsletter", result.Id);
            Assert.Equal("Unison News", result.Name);
            Assert.Equal("pic-1", result.PictureId);
            Assert.Equal("/v/t61/abc.jpg", result.PictureDirectPath);
            Assert.Equal(
                NewsletterPictureUrl.PpsHost + "/v/t61/abc.jpg",
                result.ResolvedPictureUrl);
        }

        [Fact]
        public void Falls_back_to_preview_when_picture_null()
        {
            const string json =
                "{" +
                "\"data\":{\"xwa2_newsletter\":{" +
                "\"id\":\"120363@newsletter\"," +
                "\"thread_metadata\":{" +
                "\"name\":{\"text\":\"Channel\"}," +
                "\"picture\":null," +
                "\"preview\":{\"id\":\"prev-9\",\"direct_path\":\"/v/preview.bin\"}" +
                "}}}}";

            NewsletterMetadataResult result = NewsletterMetadataWireParse.TryParse(json);

            Assert.Equal("prev-9", result.PictureId);
            Assert.Equal("/v/preview.bin", result.PictureDirectPath);
            Assert.True(result.HasPicture);
        }

        [Fact]
        public void Graphql_errors_surface_failure_reason()
        {
            const string json =
                "{\"errors\":[{\"message\":\"Forbidden\"}]}";

            NewsletterMetadataResult result = NewsletterMetadataWireParse.TryParse(json);

            Assert.Equal("Forbidden", result.FailureReason);
            Assert.False(result.HasPicture);
        }

        [Fact]
        public void Reads_the_viewer_role()
        {
            const string json =
                "{\"data\":{\"xwa2_newsletter\":{" +
                "\"id\":\"120363@newsletter\"," +
                "\"thread_metadata\":{\"name\":{\"text\":\"Mine\"}}," +
                "\"viewer_metadata\":{\"mute\":\"OFF\",\"role\":\"ADMIN\"}" +
                "}}}";

            NewsletterMetadataResult result = NewsletterMetadataWireParse.TryParse(json);

            Assert.Equal("ADMIN", result.ViewerRole);
        }

        [Fact]
        public void Create_reply_is_read_from_its_own_data_path()
        {
            const string json =
                "{\"data\":{\"xwa2_newsletter_create\":{" +
                "\"id\":\"120363000000000010@newsletter\"," +
                "\"state\":{\"type\":\"ACTIVE\"}," +
                "\"thread_metadata\":{\"name\":{\"text\":\"Unison News\"}," +
                "\"description\":{\"text\":\"Updates\"},\"invite\":\"AbC\"}," +
                "\"viewer_metadata\":{\"mute\":\"OFF\",\"role\":\"OWNER\"}" +
                "}}}";

            NewsletterMetadataResult result = NewsletterMetadataWireParse.TryParse(
                json,
                NewsletterMetadataWireParse.CreateDataPath);

            Assert.Equal("120363000000000010@newsletter", result.Id);
            Assert.Equal("Unison News", result.Name);
            Assert.Equal("OWNER", result.ViewerRole);
        }

        [Fact]
        public void Create_reply_without_thread_metadata_still_yields_the_jid()
        {
            // Baileys rc9 issue #2313: the server may omit thread_metadata on create.
            const string json =
                "{\"data\":{\"xwa2_newsletter_create\":{\"id\":\"120363000000000011@newsletter\"}}}";

            NewsletterMetadataResult result = NewsletterMetadataWireParse.TryParse(
                json,
                NewsletterMetadataWireParse.CreateDataPath);

            Assert.Equal("120363000000000011@newsletter", result.Id);
            Assert.Null(result.ViewerRole);
        }

        [Fact]
        public void Resolve_prefers_https_url_over_direct_path()
        {
            Assert.Equal(
                "https://cdn.example/p.jpg",
                NewsletterPictureUrl.Resolve("https://cdn.example/p.jpg", "/v/ignored"));
            Assert.Equal(
                NewsletterPictureUrl.PpsHost + "/v/t61/x",
                NewsletterPictureUrl.Resolve(null, "v/t61/x"));
        }
    }
}
