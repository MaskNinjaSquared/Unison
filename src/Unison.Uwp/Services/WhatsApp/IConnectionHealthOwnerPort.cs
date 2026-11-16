using System;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// What <see cref="ConnectionHealthSocketHost"/> still needs from the connection owner:
    /// catch-up state, and the reconnect / fatal-stream reactions.
    /// </summary>
    internal interface IConnectionHealthOwnerPort
    {
        bool IsHistoryCatchUpActive { get; }

        void RequestSoftReconnect(string reason);

        void LatchFatalAndReportStream(string code);
    }
}
