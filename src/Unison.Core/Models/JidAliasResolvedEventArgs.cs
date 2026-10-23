using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Args for <see cref="Contracts.WhatsApp.IWhatsAppService.OnJidAliasResolved"/>: a phone JID
    /// and a LID were newly paired. Raised only when the pair changed, not on every restatement
    /// of an alias the client already knew.
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
