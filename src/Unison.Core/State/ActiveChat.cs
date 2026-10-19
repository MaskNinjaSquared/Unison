// =============================================================================
// ActiveChat
//
// The conversation the user has open, held by its canonical address so a LID
// and a phone number for the same person both answer "open". The chat screen
// writes it; the incoming pump reads it from socket threads to decide unread
// counts and timeline refreshes, so the address is read without a lock.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class ActiveChat
    {
        private readonly Func<string, string> _canonicalOf;
        private volatile string _jid;

        public ActiveChat(Func<string, string> canonicalOf)
        {
            _canonicalOf = canonicalOf ?? throw new ArgumentNullException(nameof(canonicalOf));
        }

        /// <summary>Raised on the caller's thread after the open conversation is replaced.</summary>
        public event EventHandler<ActiveChatChangedEventArgs> Changed;

        /// <summary>Canonical address of the open conversation, or null.</summary>
        public string Jid => _jid;

        /// <summary>Opens <paramref name="jid"/>; a blank address closes the conversation.</summary>
        public void Open(string jid)
        {
            string previous = _jid;
            string current = string.IsNullOrWhiteSpace(jid) ? null : _canonicalOf(jid);
            _jid = current;
            Changed?.Invoke(this, new ActiveChatChangedEventArgs(previous, current));
        }

        public bool IsOpen(string jid)
        {
            string open = _jid;
            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(open))
            {
                return false;
            }

            return string.Equals(_canonicalOf(jid), open, StringComparison.OrdinalIgnoreCase);
        }
    }
}
