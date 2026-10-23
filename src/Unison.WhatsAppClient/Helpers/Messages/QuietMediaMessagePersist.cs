// =============================================================================
// QuietMediaMessagePersist
//
// On-demand media download writes the repaired URI back onto the row. A store
// failure must not fail the download — same swallow as the old client save.
// Persist still goes through IOutgoingMessageHost until the live queue leaves.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// <see cref="IMediaDownloadHost"/> that persists via
    /// <see cref="IOutgoingMessageHost.PersistLiveAsync"/> and never throws.
    /// </summary>
    public sealed class QuietMediaMessagePersist : IMediaDownloadHost
    {
        private readonly IOutgoingMessageHost _outgoing;

        public QuietMediaMessagePersist(IOutgoingMessageHost outgoing)
        {
            _outgoing = outgoing ?? throw new ArgumentNullException(nameof(outgoing));
        }

        public async Task SaveMessageQuietAsync(string chatJid, ChatMessage message)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || message == null)
            {
                return;
            }

            try
            {
                await _outgoing.PersistLiveAsync(chatJid, message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[QuietMedia] Failed to save message: " + ex.Message);
            }
        }
    }
}
