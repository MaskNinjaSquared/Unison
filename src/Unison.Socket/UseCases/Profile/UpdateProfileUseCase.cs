// =============================================================================
// UpdateProfileUseCase
//
// Changes our own status line and profile picture - or a group's, since a group
// picture is set through the same query with the group as the target.
//
// The picture goes up as JPEG bytes, already square and already scaled. That
// resizing needs an image codec, which is exactly the kind of thing a protocol
// layer should not carry, so the host does it and hands over the result.
//
// The display name is not here. It lives on the account's shared state rather
// than in a query, so it is set through an app state patch instead.
//
// Ports: rc14 updateProfileStatus, updateProfilePicture and
// removeProfilePicture in src/Socket/chats.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Profile
{
    public sealed class UpdateProfileUseCase
    {
        private readonly ConnectionHandler _connection;

        public UpdateProfileUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        /// <summary>Sets the "about" line other people see on our profile.</summary>
        public Task UpdateStatusAsync(string status, TimeSpan? timeout = null)
        {
            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "status" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode("status", null, Encoding.UTF8.GetBytes(status ?? string.Empty))
                });

            return _connection.QueryAsync(iq, timeout);
        }

        /// <param name="jid">
        /// Group (or other non-self) JID to change. Null/empty updates our own picture
        /// (Baileys omits <c>target</c> for self).
        /// </param>
        /// <param name="jpeg">A square JPEG. The server rejects anything else.</param>
        public Task UpdatePictureAsync(string jid, byte[] jpeg, TimeSpan? timeout = null)
        {
            if (jpeg == null || jpeg.Length == 0)
            {
                throw new ArgumentException("A picture is required", nameof(jpeg));
            }

            return _connection.QueryAsync(BuildPictureSetQuery(jid, jpeg), timeout);
        }

        /// <summary>Removes the picture. The same query with nothing in it.</summary>
        public Task RemovePictureAsync(string jid, TimeSpan? timeout = null)
        {
            return _connection.QueryAsync(BuildPictureSetQuery(jid, jpeg: null), timeout);
        }

        /// <summary>
        /// Baileys <c>updateProfilePicture</c>: <c>to</c> is always <c>s.whatsapp.net</c>;
        /// the chat/group lives in <c>target</c> (omitted for self).
        /// </summary>
        public static BinaryNode BuildPictureSetQuery(string jid, byte[] jpeg)
        {
            var attrs = new Dictionary<string, string>
            {
                { "to", WA.S_WHATSAPP_NET },
                { "type", "set" },
                { "xmlns", "w:profile:picture" }
            };

            string target = string.IsNullOrWhiteSpace(jid) ? null : JidUtils.NormalizedUser(jid);
            if (!string.IsNullOrEmpty(target))
            {
                attrs["target"] = target;
            }

            if (jpeg == null || jpeg.Length == 0)
            {
                return new BinaryNode("iq", attrs);
            }

            return new BinaryNode(
                "iq",
                attrs,
                new List<BinaryNode>
                {
                    new BinaryNode("picture", new Dictionary<string, string> { { "type", "image" } }, jpeg)
                });
        }
    }
}
