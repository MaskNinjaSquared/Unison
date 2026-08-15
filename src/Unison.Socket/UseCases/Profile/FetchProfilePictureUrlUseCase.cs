// =============================================================================
// FetchProfilePictureUrlUseCase
//
// Asks the server for the URL of a contact's or group's profile picture.
//
// This is the first UseCase and the template for the rest: one operation, one
// file, raw result in and out. It builds a node, sends it, reads the reply, and
// stops there - no domain model, no download, no cache. Turning the URL into
// something the UI can show is the facade's job, which is what keeps a UseCase
// small enough to check against the Baileys source line by line.
//
// Ports: rc14 profilePictureUrl in src/Socket/chats.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Profile
{
    /// <summary>
    /// Outcome of a picture query. A missing picture is a normal answer, not a failure, so it
    /// is reported through <see cref="IsNotFound"/> rather than by throwing.
    /// </summary>
    public sealed class ProfilePictureResult
    {
        public string Url { get; set; }

        /// <summary>Server-side picture id, usable to detect a changed avatar without downloading.</summary>
        public string Id { get; set; }

        /// <summary>True when the account has no picture, or hides it from us.</summary>
        public bool IsNotFound { get; set; }

        /// <summary>Error code when the server refused for another reason.</summary>
        public string FailureReason { get; set; }

        public bool HasUrl
        {
            get { return !string.IsNullOrEmpty(Url); }
        }
    }

    public sealed class FetchProfilePictureUrlUseCase
    {
        private readonly ConnectionHandler _connection;

        public FetchProfilePictureUrlUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        /// <param name="type">"preview" for the thumbnail, "image" for the full-size picture.</param>
        /// <param name="trustedContactToken">
        /// Required by accounts that only reveal their picture to contacts who present a token.
        /// </param>
        public async Task<ProfilePictureResult> ExecuteAsync(
            string jid,
            string type = "preview",
            byte[] trustedContactToken = null,
            TimeSpan? timeout = null)
        {
            if (string.IsNullOrEmpty(jid))
            {
                throw new ArgumentException("jid is required", nameof(jid));
            }

            // A picture belongs to an account, not to one of its devices. Our own JID always
            // carries a device suffix, so asking with it verbatim is how the account that
            // certainly has a picture - the user's own - was the one that never got one.
            var target = JidUtils.NormalizedUser(jid);
            if (string.IsNullOrEmpty(target))
            {
                target = jid;
            }

            var pictureChildren = new List<BinaryNode>();
            if (trustedContactToken != null && trustedContactToken.Length > 0)
            {
                pictureChildren.Add(new BinaryNode("tctoken", null, trustedContactToken));
            }

            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "target", target },
                    { "type", "get" },
                    { "xmlns", "w:profile:picture" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "picture",
                        new Dictionary<string, string> { { "type", type }, { "query", "url" } },
                        pictureChildren.Count > 0 ? pictureChildren : null)
                });

            BinaryNode response;
            try
            {
                // Most contacts either have no picture or hide it, so the refusal is read off
                // the reply rather than raised: this runs once per chat on every sync.
                response = await _connection.QueryAllowingErrorAsync(iq, timeout).ConfigureAwait(false);
            }
            catch (WaConnectionException ex)
            {
                var code = ((int)ex.Reason).ToString();
                return new ProfilePictureResult
                {
                    IsNotFound = IsMissingPictureCode(code),
                    FailureReason = IsMissingPictureCode(code) ? null : code
                };
            }

            var picture = response != null ? response.GetChild("picture") : null;
            if (picture != null)
            {
                var url = picture.GetAttribute("url");
                if (!string.IsNullOrEmpty(url))
                {
                    return new ProfilePictureResult { Url = url, Id = picture.GetAttribute("id") };
                }
            }

            var errorCode = ReadErrorCode(response);
            return new ProfilePictureResult
            {
                IsNotFound = IsMissingPictureCode(errorCode),
                FailureReason = IsMissingPictureCode(errorCode) ? null : errorCode
            };
        }

        private static string ReadErrorCode(BinaryNode response)
        {
            var error = response != null ? response.GetChild("error") : null;
            return error != null ? error.GetAttribute("code") : null;
        }

        /// <summary>404 means no picture, 406 means it is hidden from us. Neither is an error.</summary>
        private static bool IsMissingPictureCode(string code)
        {
            return code == "404" || code == "406";
        }
    }
}
