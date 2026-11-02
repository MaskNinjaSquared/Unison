// =============================================================================
// IIncomingPumpControl
//
// Stall / reset / restart / drain for the decrypted inbound queue. Connection
// resume and lifecycle hosts call this; they do not own the pump.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IIncomingPumpControl
    {
        void RestartIfNeeded();

        void Reset(string reason, bool requeueCurrent);

        bool IsStalled(TimeSpan limit);

        Task WaitForDrainAsync(int timeoutMs);

        string Stage { get; }
    }
}
