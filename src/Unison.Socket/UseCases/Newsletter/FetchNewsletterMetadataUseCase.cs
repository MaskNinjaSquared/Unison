// =============================================================================
// FetchNewsletterMetadataUseCase
//
// Channel (@newsletter) identity via WMex newsletterMetadata — name + picture
// direct_path. Contacts/groups keep using w:profile:picture; newsletters do not.
//
// Ports: rc14 newsletterMetadata in src/Socket/newsletter.ts + mex.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Newsletter
{
    public sealed class FetchNewsletterMetadataUseCase
    {
        /// <summary>Baileys QueryIds.METADATA</summary>
        public const string QueryId = "6563316087068696";

        private readonly ConnectionHandler _connection;

        public FetchNewsletterMetadataUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        /// <param name="key">Newsletter JID or invite code.</param>
        /// <param name="type">"jid" or "invite" (Baileys uppercases for the GraphQL input).</param>
        public async Task<NewsletterMetadataResult> ExecuteAsync(
            string key,
            string type = "jid",
            TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return new NewsletterMetadataResult
                {
                    IsNotFound = true,
                    FailureReason = "empty-key"
                };
            }

            string kind = string.IsNullOrWhiteSpace(type) ? "jid" : type.Trim().ToLowerInvariant();
            if (kind != "jid" && kind != "invite")
            {
                kind = "jid";
            }

            BinaryNode iq = BuildQuery(key.Trim(), kind);
            BinaryNode response;
            try
            {
                response = await _connection.QueryAllowingErrorAsync(iq, timeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new NewsletterMetadataResult
                {
                    FailureReason = ex.GetBaseException().Message
                };
            }

            BinaryNode error = response != null ? response.GetChild("error") : null;
            if (error != null)
            {
                string code = error.GetAttribute("code");
                return new NewsletterMetadataResult
                {
                    IsNotFound = code == "404" || code == "406",
                    FailureReason = code ?? "iq-error"
                };
            }

            BinaryNode resultNode = response != null ? response.GetChild("result") : null;
            string json = resultNode != null ? resultNode.GetContentString() : null;
            NewsletterMetadataResult parsed = NewsletterMetadataWireParse.TryParse(json);
            if (parsed == null)
            {
                return new NewsletterMetadataResult
                {
                    IsNotFound = true,
                    FailureReason = "parse-null"
                };
            }

            if (string.IsNullOrEmpty(parsed.Id) &&
                string.IsNullOrEmpty(parsed.Name) &&
                !parsed.HasPicture &&
                string.IsNullOrEmpty(parsed.FailureReason))
            {
                parsed.IsNotFound = true;
                parsed.FailureReason = "empty-metadata";
            }

            return parsed;
        }

        internal static BinaryNode BuildQuery(string key, string type)
        {
            // GraphQL variables match Baileys newsletterMetadata exactly.
            string variablesJson =
                "{\"variables\":{" +
                "\"fetch_creation_time\":true," +
                "\"fetch_full_image\":true," +
                "\"fetch_viewer_metadata\":true," +
                "\"input\":{" +
                "\"key\":" + QuoteJson(key) + "," +
                "\"type\":" + QuoteJson(type.ToUpperInvariant()) +
                "}}}";

            return new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "get" },
                    { "xmlns", "w:mex" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "query",
                        new Dictionary<string, string> { { "query_id", QueryId } },
                        Encoding.UTF8.GetBytes(variablesJson))
                });
        }

        private static string QuoteJson(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}
