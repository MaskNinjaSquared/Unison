// =============================================================================
// PreKeyUploadPlanDecision
//
// Which one-time prekeys the next upload batch announces: the ones already
// minted and stored but never acknowledged by the server first, then fresh ids
// only for the gap.
//
// Without this every failed upload minted a whole new batch on top of the last
// one (Lumia 512, 8e31b77c: 812 -> 1498 -> 1868 keys on disk, none on the
// server), so a device too slow to finish one batch could never catch up.
//
// Ports: rc14 getNextPreKeys / firstUnuploadedPreKeyId in src/Utils/signal.ts.
// The watermark moves only after the server acks, not before as rc14 does.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace Unison.Socket.UseCases.Auth
{
    public sealed class PreKeyUploadPlan
    {
        public PreKeyUploadPlan(IReadOnlyList<int> reuseIds, int firstNewId, int mintCount)
        {
            ReuseIds = reuseIds ?? new int[0];
            FirstNewId = firstNewId;
            MintCount = mintCount;
        }

        /// <summary>Stored, never-acked ids to announce again, ascending.</summary>
        public IReadOnlyList<int> ReuseIds { get; }

        /// <summary>First id to mint; above every id already issued or stored.</summary>
        public int FirstNewId { get; }

        public int MintCount { get; }

        /// <summary>The issued-id counter once the minted keys exist.</summary>
        public int LastIssuedIdAfterMint => FirstNewId + MintCount - 1;
    }

    public static class PreKeyUploadPlanDecision
    {
        /// <param name="firstUnuploadedId">Lowest id not yet acked; zero on installs that predate the watermark.</param>
        /// <param name="lastIssuedId">The auth state's prekey counter (the last id handed out).</param>
        /// <param name="storedIds">Ids whose private half is still in the key store.</param>
        public static PreKeyUploadPlan Plan(
            int firstUnuploadedId,
            int lastIssuedId,
            int count,
            IEnumerable<int> storedIds)
        {
            int[] stored = (storedIds ?? Enumerable.Empty<int>()).ToArray();

            // The counter is saved with the credentials and can trail the key store after a
            // crash; minting from it would overwrite keys that are already on disk.
            int highestKnown = stored.Length > 0
                ? Math.Max(lastIssuedId, stored.Max())
                : lastIssuedId;
            int firstNewId = highestKnown + 1;

            if (count <= 0)
            {
                return new PreKeyUploadPlan(new int[0], firstNewId, 0);
            }

            int floor = Math.Max(firstUnuploadedId, 1);
            int[] reuse = stored
                .Where(id => id >= floor)
                .Distinct()
                .OrderBy(id => id)
                .Take(count)
                .ToArray();

            return new PreKeyUploadPlan(reuse, firstNewId, count - reuse.Length);
        }

        /// <summary>Watermark after the server acked a batch whose highest id is <paramref name="highestAckedId"/>.</summary>
        public static int FirstUnuploadedAfterAck(int currentFirstUnuploadedId, int highestAckedId) =>
            Math.Max(currentFirstUnuploadedId, highestAckedId + 1);
    }
}
