// =============================================================================
// ChatListDeepLinkHoldGate
//
// Toast / tile activation: keep the chat-list ListView (x:Load) unmaterialized
// until the conversation pane has loaded, then release so the list can paint.
// Never tears down a list that already existed (warm start) — ChatListSyncHold
// ignores this gate once the list has materialized once this session.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace Unison.Core.State
{
    public sealed class ChatListDeepLinkHoldGate
    {
        private int _held;

        public bool IsHeld => Volatile.Read(ref _held) != 0;

        public event EventHandler Changed;

        public void Hold(string reason)
        {
            if (Interlocked.Exchange(ref _held, 1) == 1)
            {
                return;
            }

            Debug.WriteLine("[ChatListDeepLinkHoldGate] Hold (" + (reason ?? string.Empty) + ")");
            RaiseChanged();
        }

        public void Release(string reason)
        {
            if (Interlocked.Exchange(ref _held, 0) == 0)
            {
                return;
            }

            Debug.WriteLine("[ChatListDeepLinkHoldGate] Release (" + (reason ?? string.Empty) + ")");
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatListDeepLinkHoldGate] Listener failed: " + ex.Message);
            }
        }
    }
}
