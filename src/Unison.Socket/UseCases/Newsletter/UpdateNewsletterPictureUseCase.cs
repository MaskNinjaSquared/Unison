// =============================================================================
// UpdateNewsletterPictureUseCase
//
// Sets a channel's picture: a square JPEG, base64 in updates.picture. Channels
// do not take w:profile:picture like groups do.
//
// Ports: rc14 newsletterUpdatePicture / newsletterUpdate in src/Socket/newsletter.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Newsletter
{
    public sealed class UpdateNewsletterPictureUseCase
    {
        /// <summary>Baileys QueryIds.UPDATE_METADATA</summary>
        public const string QueryId = "24250201037901610";

        private const string DataPath = "xwa2_newsletter_update";

        private readonly ConnectionHandler _connection;

        public UpdateNewsletterPictureUseCase(ConnectionHandler connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <exception cref="InvalidOperationException">The server refused the update.</exception>
        public async Task ExecuteAsync(string newsletterJid, byte[] jpeg, TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(newsletterJid))
            {
                throw new ArgumentException("A channel JID is required", nameof(newsletterJid));
            }

            if (jpeg == null || jpeg.Length == 0)
            {
                throw new ArgumentException("A JPEG is required", nameof(jpeg));
            }

            NewsletterMetadataResult answer = await WMexQuery.ExecuteAsync(
                _connection,
                BuildQuery(newsletterJid.Trim(), jpeg),
                DataPath,
                timeout).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(answer.FailureReason) && !answer.IsNotFound)
            {
                throw new InvalidOperationException("Channel picture refused: " + answer.FailureReason);
            }
        }

        internal static BinaryNode BuildQuery(string newsletterJid, byte[] jpeg)
        {
            var variables = new JObject
            {
                ["newsletter_id"] = newsletterJid,
                ["updates"] = new JObject
                {
                    ["picture"] = Convert.ToBase64String(jpeg),
                    ["settings"] = JValue.CreateNull()
                }
            };
            return WMexQuery.Build(QueryId, variables);
        }
    }
}
