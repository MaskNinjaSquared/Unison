using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Background;
using Unison.Baileys.Protocol;

namespace Unison.Uwp.Transport
{
    internal sealed class NoiseSessionSnapshot
    {
        internal string SocketId { get; set; }
        internal DateTime SavedUtc { get; set; }
        internal NoiseSessionState State { get; set; }
    }

    internal static class NoiseSessionStore
    {
        internal static async Task SaveAsync(
            NoiseSessionState state,
            string socketId)
        {
            await BrokerNoiseSessionStore.SaveAsync(state, socketId);
        }

        internal static async Task<NoiseSessionSnapshot> LoadSnapshotAsync()
        {
            BrokerOwnershipState ownership =
                await BrokerOwnershipStore.LoadAsync();
            string currentSocketId = ownership?.SocketId;
            BrokerNoiseSessionSnapshot stored =
                await BrokerNoiseSessionStore.LoadSnapshotAsync();
            NoiseSessionSnapshot effective =
                stored != null &&
                string.Equals(
                    stored.SocketId,
                    currentSocketId,
                    StringComparison.Ordinal)
                    ? new NoiseSessionSnapshot
                    {
                        SocketId = stored.SocketId,
                        SavedUtc = stored.SavedUtc,
                        State = stored.State
                    }
                    : null;

            IList<BrokerJournalPendingEntry> pending =
                await BrokerFrameJournal.ReadPendingAsync();
            foreach (BrokerJournalPendingEntry entry in pending)
            {
                BrokerDecodedFrameBatch batch;
                if (!BrokerDecodedFrameEnvelope.TryUnpack(
                        entry.Payload,
                        out batch) ||
                    !string.Equals(
                        batch.SocketId,
                        currentSocketId,
                        StringComparison.Ordinal) ||
                    batch.PostNoiseState == null)
                {
                    continue;
                }

                if (effective == null ||
                    batch.PostNoiseState.ReadCounter >=
                    effective.State.ReadCounter)
                {
                    effective = new NoiseSessionSnapshot
                    {
                        SocketId = batch.SocketId,
                        SavedUtc = batch.CreatedUtc,
                        State = batch.PostNoiseState
                    };
                }
            }

            return effective;
        }

        internal static async Task ClearAsync()
        {
            await BrokerNoiseSessionStore.ClearAsync();
        }
    }
}
