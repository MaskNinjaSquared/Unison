using System;
using Unison.Uwp.Client;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// What <see cref="ConnectionHealthSocketHost"/> still needs from the connection owner:
    /// the current socket slot, catch-up state, and the reconnect / fatal-stream reactions.
    /// </summary>
    internal interface IConnectionHealthOwnerPort
    {
        IWhatsAppSocket CurrentSocket { get; }

        void ClearCurrentSocketIf(IWhatsAppSocket socket);

        bool IsHistoryCatchUpActive { get; }

        void RequestSoftReconnect(string reason);

        void LatchFatalAndReportStream(string code);
    }
}
