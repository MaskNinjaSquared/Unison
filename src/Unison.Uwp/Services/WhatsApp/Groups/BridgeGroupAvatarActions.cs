// =============================================================================
// BridgeGroupAvatarActions
//
// Forwards IGroupAvatarActions to AvatarFetcher (UWP download + disk cache).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Uwp.Services.WhatsApp.Contacts;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.Uwp.Services.WhatsApp.Groups
{
    internal sealed class BridgeGroupAvatarActions : IGroupAvatarActions
    {
        private readonly AvatarFetcher _avatars;

        public BridgeGroupAvatarActions(AvatarFetcher avatars)
        {
            _avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
        }

        public Task<string> FetchHighResolutionAsync(
            string jid,
            IEnumerable<string> candidates,
            CancellationToken token)
        {
            return _avatars.FetchHighResolutionAsync(jid, candidates, token);
        }

        public Task<string> CachePreviewAsync(string jid, string remoteUrl, CancellationToken token)
        {
            return _avatars.CachePreviewAsync(jid, remoteUrl, token);
        }
    }
}
