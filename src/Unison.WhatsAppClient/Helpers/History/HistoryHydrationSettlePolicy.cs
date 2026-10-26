// =============================================================================
// HistoryHydrationSettlePolicy
//
// Timing for WaitForHistoryHydrationSettleAsync — lets in-flight chat history
// requests drain before connection work continues.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistoryHydrationSettlePolicy
    {
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(25);

        public static readonly TimeSpan IdleConfirmDelay = TimeSpan.FromMilliseconds(400);

        public static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(500);
    }
}
