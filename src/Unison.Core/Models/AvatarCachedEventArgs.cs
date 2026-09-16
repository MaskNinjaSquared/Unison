using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Args for <see cref="Contracts.WhatsApp.IWhatsAppService.OnAvatarCached"/>: an avatar file
    /// is now on disk for this JID. Reports the fact only — whether that becomes a Person write
    /// is the contacts facade's call.
    /// </summary>
    public sealed class AvatarCachedEventArgs : EventArgs
    {
        public AvatarCachedEventArgs(string jid, string localAvatarUrl)
        {
            Jid = jid;
            LocalAvatarUrl = localAvatarUrl;
        }

        public string Jid { get; }

        public string LocalAvatarUrl { get; }
    }
}
