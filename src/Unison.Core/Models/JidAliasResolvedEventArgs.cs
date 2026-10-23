using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Legacy args shape for a newly paired phone JID and LID. Host paths now call
    /// <see cref="Contracts.WhatsApp.IContactService.NotifyJidAliasResolved"/> directly.
    /// </summary>
    public sealed class JidAliasResolvedEventArgs : EventArgs
    {
        public JidAliasResolvedEventArgs(string phoneJid, string lidJid, string source = null)
        {
            PhoneJid = phoneJid;
            LidJid = lidJid;
            Source = source;
        }

        public string PhoneJid { get; }

        public string LidJid { get; }

        /// <summary>Who filed the pair (live message, history, usync, …). Informs the follow-up log.</summary>
        public string Source { get; }
    }
}
