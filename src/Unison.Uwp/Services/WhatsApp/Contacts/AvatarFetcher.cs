// =============================================================================
// AvatarFetcher
//
// Getting a picture off the wire and onto disk. The other half of avatars:
// ChatAvatarPolicy decides when to ask, this does the asking, and applying the
// answer to a ChatItem row stays with whoever owns the row.
//
// Nothing here reads or writes chat state, which is the whole point - it can be
// called from a background batch, a row scrolling into view, or the group info
// pivot without any of them having to agree about threads.
//
// Candidates arrive from the caller rather than being resolved here. The PN/LID
// table is still the client's until phase 3.7, and reaching for it would put a
// cycle between this and IJidResolver.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Uwp.Client;
using Unison.Uwp.Services.Socket;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    internal sealed class AvatarFetcher
    {
        private const string PreviewQueryType = "preview";
        private const string FullSizeQueryType = "image";

        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IUsyncGate _gate;
        private readonly IAvatarCache _cache;

        internal AvatarFetcher(IWhatsAppSessionProvider sessions, IUsyncGate gate, IAvatarCache cache)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        /// <summary>
        /// Asks each candidate in turn and stops at the first one with a picture. The result is
        /// returned rather than applied: only the caller knows what a miss means for its row.
        /// </summary>
        public async Task<ProfilePictureResult> FetchPreviewAsync(
            IEnumerable<string> candidates,
            CancellationToken token)
        {
            ProfilePictureResult lastResult = null;
            foreach (var candidate in candidates ?? Enumerable.Empty<string>())
            {
                token.ThrowIfCancellationRequested();

                // Avatar refreshes are queued in the background and outlive the connection they
                // were queued against, so the socket can be gone by the time one runs. That is an
                // ordinary "try again later", not a failure worth crashing over.
                var socket = ReadySocket;
                if (socket == null)
                {
                    return new ProfilePictureResult
                    {
                        TargetJid = candidate,
                        FailureReason = "not-connected"
                    };
                }

                using (await _gate.AcquireAsync(token).ConfigureAwait(false))
                {
                    lastResult = await socket.GetProfilePictureUrlResultAsync(candidate, PreviewQueryType);
                }

                Debug.WriteLine(
                    $"[AvatarFetcher] Candidate result: candidate={candidate}, target={lastResult?.TargetJid}, " +
                    $"hasUrl={!string.IsNullOrWhiteSpace(lastResult?.Url)}, reason={lastResult?.FailureReason}");

                if (!string.IsNullOrWhiteSpace(lastResult?.Url))
                {
                    return lastResult;
                }
            }

            return lastResult ?? new ProfilePictureResult
            {
                IsNotFound = true,
                FailureReason = "no-picture-candidates"
            };
        }

        /// <summary>Stores a picture already located by <see cref="FetchPreviewAsync"/> or a fallback.</summary>
        public Task<string> CachePreviewAsync(string jid, string remoteUrl, CancellationToken token)
        {
            return _cache.SaveAsync(jid, remoteUrl, AvatarVariant.Preview, token);
        }

        /// <summary>
        /// The full-sized copy for group art the user opened, served from disk when it is already
        /// there. Returns the local URI, or null when no candidate has one.
        /// </summary>
        public async Task<string> FetchHighResolutionAsync(
            string jid,
            IEnumerable<string> candidates,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string cached;
            DateTime fetchedAtUtc;
            if (_cache.TryGet(jid, AvatarVariant.HighResolution, out cached, out fetchedAtUtc))
            {
                return cached;
            }

            if (ReadySocket == null)
            {
                return null;
            }

            foreach (var candidate in candidates ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var socket = ReadySocket;
                if (socket == null)
                {
                    return null;
                }

                ProfilePictureResult result;
                using (await _gate.AcquireAsync(token).ConfigureAwait(false))
                {
                    result = await socket.GetProfilePictureUrlResultAsync(candidate, FullSizeQueryType);
                }

                if (string.IsNullOrWhiteSpace(result?.Url))
                {
                    continue;
                }

                try
                {
                    string localUri = await _cache
                        .SaveAsync(jid, result.Url, AvatarVariant.HighResolution, token)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(localUri))
                    {
                        continue;
                    }

                    Debug.WriteLine($"[AvatarFetcher] Cached high-res avatar for {jid}");
                    return localUri;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AvatarFetcher] High-res avatar failed for {jid}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>A remote URL only, with no download. Null when unavailable.</summary>
        public async Task<string> FetchUrlAsync(string jid, string type = PreviewQueryType)
        {
            var socket = ReadySocket;
            if (string.IsNullOrWhiteSpace(jid) || socket == null)
            {
                return null;
            }

            try
            {
                var result = await socket.GetProfilePictureUrlResultAsync(jid, type).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(result?.Url) ? null : result.Url;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvatarFetcher] FetchUrlAsync failed for {jid}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// One JID, fetched and stored. Falls back to the remote URL when the download fails, so
        /// the caller still has something to show.
        /// </summary>
        public async Task<string> FetchAndCacheAsync(string jid, CancellationToken token)
        {
            var socket = ReadySocket;
            if (string.IsNullOrWhiteSpace(jid) || socket == null)
            {
                return null;
            }

            var result = await socket.GetProfilePictureUrlResultAsync(jid, FullSizeQueryType);
            if (string.IsNullOrWhiteSpace(result?.Url))
            {
                Debug.WriteLine(
                    $"[AvatarFetcher] No picture for {jid}: target={result?.TargetJid}, " +
                    $"lookup={result?.TokenLookupJid}, reason={result?.FailureReason}");
                return null;
            }

            try
            {
                return await _cache.SaveAsync(jid, result.Url, AvatarVariant.Preview, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvatarFetcher] Cache failed for {jid}: {ex.Message}");
                return result.Url;
            }
        }

        /// <summary>
        /// Read once per use: the socket is replaced on every reconnect, so a handshake checked
        /// against one instance says nothing about the next.
        /// </summary>
        private IWhatsAppSocket ReadySocket
        {
            get
            {
                var socket = _sessions.Socket;
                return socket != null && socket.IsHandshakeComplete ? socket : null;
            }
        }
    }
}
