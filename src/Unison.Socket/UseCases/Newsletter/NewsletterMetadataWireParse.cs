// =============================================================================
// NewsletterMetadataWireParse
//
// Turns the WMex JSON body into a flat NewsletterMetadataResult. Real replies
// use thread_metadata (name.text, picture|preview.direct_path); Baileys also
// documents a flatter shape — both are accepted.
// Ports: rc14 newsletter.ts parseNewsletterCreateResponse / parseNewsletterMetadata
// =============================================================================
using System;
using Newtonsoft.Json.Linq;

namespace Unison.Socket.UseCases.Newsletter
{
    public static class NewsletterMetadataWireParse
    {
        public const string DataPath = "xwa2_newsletter";

        /// <summary>Baileys <c>XWAPaths.xwa2_newsletter_create</c>.</summary>
        public const string CreateDataPath = "xwa2_newsletter_create";

        public static NewsletterMetadataResult TryParse(string json)
        {
            return TryParse(json, DataPath);
        }

        public static NewsletterMetadataResult TryParse(string json, string dataPath)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new NewsletterMetadataResult
                {
                    IsNotFound = true,
                    FailureReason = "empty-json"
                };
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception)
            {
                return new NewsletterMetadataResult
                {
                    FailureReason = "invalid-json"
                };
            }

            JToken errors = root["errors"];
            if (errors is JArray errorArray && errorArray.Count > 0)
            {
                string message = errorArray[0]?["message"]?.ToString();
                return new NewsletterMetadataResult
                {
                    FailureReason = string.IsNullOrWhiteSpace(message) ? "graphql-error" : message
                };
            }

            JToken payload = root["data"]?[dataPath] ?? root[dataPath] ?? root;
            if (payload == null || payload.Type == JTokenType.Null)
            {
                return new NewsletterMetadataResult
                {
                    IsNotFound = true,
                    FailureReason = "missing-payload"
                };
            }

            if (payload["result"] is JObject nested && nested["id"] != null)
            {
                payload = nested;
            }

            if (!(payload is JObject obj))
            {
                return new NewsletterMetadataResult
                {
                    IsNotFound = true,
                    FailureReason = "unexpected-payload"
                };
            }

            return FromObject(obj);
        }

        internal static NewsletterMetadataResult FromObject(JObject obj)
        {
            var result = new NewsletterMetadataResult
            {
                Id = TokenString(obj["id"]),
                ViewerRole = TokenString(obj["viewer_metadata"]?["role"])
            };

            JObject thread = obj["thread_metadata"] as JObject;
            if (thread != null)
            {
                result.Name = TokenString(thread["name"]?["text"]) ?? TokenString(thread["name"]);
                ApplyPicture(result, thread["picture"] as JObject);
                if (!result.HasPicture)
                {
                    ApplyPicture(result, thread["preview"] as JObject);
                }

                return result;
            }

            result.Name = TokenString(obj["name"]?["text"]) ?? TokenString(obj["name"]);
            ApplyPicture(result, obj["picture"] as JObject);
            if (!result.HasPicture)
            {
                ApplyPicture(result, obj["preview"] as JObject);
            }

            return result;
        }

        private static void ApplyPicture(NewsletterMetadataResult result, JObject picture)
        {
            if (result == null || picture == null)
            {
                return;
            }

            string id = TokenString(picture["id"]);
            string url = TokenString(picture["url"]);
            string directPath = TokenString(picture["direct_path"]) ??
                                TokenString(picture["directPath"]);

            if (!string.IsNullOrWhiteSpace(id))
            {
                result.PictureId = id;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                result.PictureUrl = url;
            }

            if (!string.IsNullOrWhiteSpace(directPath))
            {
                result.PictureDirectPath = directPath;
            }
        }

        private static string TokenString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                string value = token.Value<string>();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            if (token.Type == JTokenType.Object)
            {
                return null;
            }

            string text = token.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }
}
