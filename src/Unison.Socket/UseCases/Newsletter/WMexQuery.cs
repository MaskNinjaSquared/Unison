// =============================================================================
// WMexQuery
//
// The iq that carries a WhatsApp GraphQL ("mex") call: a query_id on a <query>
// child whose body is {"variables": ...} as UTF-8 JSON. Answers come back as
// JSON in the <result> child, or as an iq <error>.
//
// Ports: rc14 executeWMexQuery in src/Socket/mex.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Newsletter
{
    internal static class WMexQuery
    {
        public static BinaryNode Build(string queryId, JObject variables)
        {
            var body = new JObject { ["variables"] = variables ?? new JObject() };
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
                        new Dictionary<string, string> { { "query_id", queryId } },
                        Encoding.UTF8.GetBytes(body.ToString(Formatting.None)))
                });
        }

        /// <summary>
        /// Sends <paramref name="iq"/> and parses the answer at <paramref name="dataPath"/>.
        /// Never throws for a refused query; the reason lands on
        /// <see cref="NewsletterMetadataResult.FailureReason"/>.
        /// </summary>
        public static async Task<NewsletterMetadataResult> ExecuteAsync(
            ConnectionHandler connection,
            BinaryNode iq,
            string dataPath,
            TimeSpan? timeout)
        {
            BinaryNode response;
            try
            {
                response = await connection.QueryAllowingErrorAsync(iq, timeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new NewsletterMetadataResult { FailureReason = ex.GetBaseException().Message };
            }

            BinaryNode error = response != null ? response.GetChild("error") : null;
            if (error != null)
            {
                return new NewsletterMetadataResult
                {
                    FailureReason = error.GetAttribute("code") ?? "iq-error"
                };
            }

            BinaryNode resultNode = response != null ? response.GetChild("result") : null;
            string json = resultNode != null ? resultNode.GetContentString() : null;
            return NewsletterMetadataWireParse.TryParse(json, dataPath)
                ?? new NewsletterMetadataResult { FailureReason = "parse-null" };
        }
    }
}
