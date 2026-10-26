using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Legacy args shape for a local avatar cache write. Host paths now call
    /// <see cref="Contracts.WhatsApp.IContactService.NotifyAvatarCached"/> directly.
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
