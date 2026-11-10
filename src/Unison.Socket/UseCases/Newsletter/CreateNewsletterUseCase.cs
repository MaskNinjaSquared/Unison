// =============================================================================
// CreateNewsletterUseCase
//
// Creates a WhatsApp Channel. The creator comes back as OWNER in
// viewer_metadata; the picture is a separate update once the JID exists.
//
// Ports: rc14 newsletterCreate in src/Socket/newsletter.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Newsletter
{
    public sealed class CreateNewsletterUseCase
    {
        /// <summary>Baileys QueryIds.CREATE</summary>
        public const string QueryId = "8823471724422422";

        private readonly ConnectionHandler _connection;

        public CreateNewsletterUseCase(ConnectionHandler connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <returns>The new channel; <see cref="NewsletterMetadataResult.Id"/> is null when refused.</returns>
        public async Task<NewsletterMetadataResult> ExecuteAsync(
            string name,
            string description,
            TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return new NewsletterMetadataResult { FailureReason = "empty-name" };
            }

            NewsletterMetadataResult created = await WMexQuery.ExecuteAsync(
                _connection,
                BuildQuery(name.Trim(), description),
                NewsletterMetadataWireParse.CreateDataPath,
                timeout).ConfigureAwait(false);

            if (string.IsNullOrEmpty(created.Id) && string.IsNullOrEmpty(created.FailureReason))
            {
                created.FailureReason = "no-channel";
            }

            return created;
        }

        internal static BinaryNode BuildQuery(string name, string description)
        {
            JToken descriptionToken = string.IsNullOrWhiteSpace(description)
                ? JValue.CreateNull()
                : new JValue(description.Trim());
            var variables = new JObject
            {
                ["input"] = new JObject
                {
                    ["name"] = name,
                    ["description"] = descriptionToken
                }
            };
            return WMexQuery.Build(QueryId, variables);
        }
    }
}
