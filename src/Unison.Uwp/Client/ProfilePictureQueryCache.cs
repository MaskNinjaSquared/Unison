// =============================================================================
// ProfilePictureQueryCache
//
// The w:profile:picture IQ answers, kept across reconnects (DI singleton) so a
// list batch, an alias follow-up and a roster pass do not ask again for the
// same JID. TTLs: ProfilePictureCachePolicy. A revalidating read (chat open,
// broken image) goes to the server and writes the answer back. A `picture`
// notification drops the JID's answers.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.State;
using Unison.Socket.Utils;

namespace Unison.Uwp.Client
{
    public sealed class ProfilePictureQueryCache
    {
        private static readonly string[] QueryTypes = { "preview", "image" };

        private readonly AsyncQueryCache<ProfilePictureResult> _answers;

        public ProfilePictureQueryCache(SyncWorkloadStrategy workload)
        {
            if (workload == null)
            {
                throw new ArgumentNullException(nameof(workload));
            }

            _answers = new AsyncQueryCache<ProfilePictureResult>(
                r => ProfilePictureCachePolicy.TtlFor(!string.IsNullOrWhiteSpace(r.Url), r.IsNotFound),
                ProfilePictureCachePolicy.RevalidateCooldown,
                workload.ProfilePictureQueryCacheMaxEntries);
        }

        public int Count => _answers.Count;

        public async Task<ProfilePictureResult> GetAsync(
            string jid,
            string type,
            bool revalidate,
            Func<Task<ProfilePictureResult>> query)
        {
            ProfilePictureResult shared = await _answers
                .GetAsync(Key(jid, type), revalidate, query)
                .ConfigureAwait(false);
            return Copy(shared);
        }

        public void Invalidate(string jid)
        {
            for (int i = 0; i < QueryTypes.Length; i++)
            {
                _answers.Invalidate(Key(jid, QueryTypes[i]));
            }
        }

        private static string Key(string jid, string type) =>
            (JidHelper.Normalize(jid) ?? jid ?? string.Empty) + "|" + (type ?? string.Empty);

        // Callers stamp the result they get; the cached answer must stay as the server sent it.
        private static ProfilePictureResult Copy(ProfilePictureResult r)
        {
            if (r == null)
            {
                return null;
            }

            return new ProfilePictureResult
            {
                Url = r.Url,
                Id = r.Id,
                TargetJid = r.TargetJid,
                TokenLookupJid = r.TokenLookupJid,
                IsNotFound = r.IsNotFound,
                IsTimeout = r.IsTimeout,
                FailureReason = r.FailureReason,
            };
        }
    }
}
