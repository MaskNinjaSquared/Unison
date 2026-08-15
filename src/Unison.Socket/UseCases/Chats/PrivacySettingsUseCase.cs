// =============================================================================
// PrivacySettingsUseCase
//
// Reads and writes the account's privacy settings.
//
// Every setting is the same query with a different category name, so they are
// one use case rather than seven. The values are the strings the server uses -
// "all", "contacts", "contact_blacklist", "none", "match_last_seen" - and they
// are passed through rather than mapped, because which values a category
// accepts differs between them and the server is the only authority on it.
//
// The default disappearing timer is here too. It reads like a privacy setting
// to the user, though it travels under its own namespace.
//
// Ports: rc14 fetchPrivacySettings, privacyQuery and
// updateDefaultDisappearingMode in src/Socket/chats.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Chats
{
    public static class PrivacyCategory
    {
        public const string LastSeen = "last";
        public const string Online = "online";
        public const string ProfilePicture = "profile";
        public const string Status = "status";
        public const string ReadReceipts = "readreceipts";
        public const string GroupAdd = "groupadd";
        public const string CallAdd = "calladd";
    }

    public sealed class PrivacySettingsUseCase
    {
        private readonly ConnectionHandler _connection;

        public PrivacySettingsUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        /// <summary>Returns each category and the value it is set to.</summary>
        public async Task<Dictionary<string, string>> FetchAsync(TimeSpan? timeout = null)
        {
            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "get" },
                    { "xmlns", "privacy" }
                },
                new List<BinaryNode> { new BinaryNode("privacy") });

            var response = await _connection.QueryAsync(iq, timeout).ConfigureAwait(false);

            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            var privacy = response != null ? response.GetChild("privacy") : null;
            if (privacy == null)
            {
                return settings;
            }

            var categories = privacy.GetChildren("category");
            if (categories != null)
            {
                foreach (var category in categories)
                {
                    var name = category.GetAttribute("name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        settings[name] = category.GetAttribute("value");
                    }
                }
            }

            return settings;
        }

        /// <param name="category">One of the names in <see cref="PrivacyCategory"/>.</param>
        /// <param name="value">"all", "contacts", "contact_blacklist", "none" and so on.</param>
        public Task UpdateAsync(string category, string value, TimeSpan? timeout = null)
        {
            if (string.IsNullOrEmpty(category))
            {
                throw new ArgumentException("A category is required", nameof(category));
            }

            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "privacy" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "privacy",
                        null,
                        new List<BinaryNode>
                        {
                            new BinaryNode(
                                "category",
                                new Dictionary<string, string> { { "name", category }, { "value", value } })
                        })
                });

            return _connection.QueryAsync(iq, timeout);
        }

        /// <param name="seconds">How long new messages last by default, or zero to turn it off.</param>
        public Task UpdateDefaultDisappearingModeAsync(int seconds, TimeSpan? timeout = null)
        {
            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "disappearing_mode" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "disappearing_mode",
                        new Dictionary<string, string> { { "duration", seconds.ToString() } })
                });

            return _connection.QueryAsync(iq, timeout);
        }
    }
}
