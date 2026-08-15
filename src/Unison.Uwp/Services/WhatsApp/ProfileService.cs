using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Me profile hydrate (auth) vs sync (WhatsApp IQ). Keeps this out of WhatsAppService.
    /// </summary>
    public sealed class ProfileService : IProfileService
    {
        private readonly IWhatsAppService _whatsAppService;

        public ProfileService(IWhatsAppService whatsAppService)
        {
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
        }

        public Profile GetCurrentProfile()
        {
            var profile = _whatsAppService.CurrentProfile;
            if (profile == null)
            {
                return new Profile();
            }

            string name = profile.Name;
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(profile.Id))
            {
                name = profile.Id.Split('@')[0].Split(':')[0];
            }

            return new Profile
            {
                Id = profile.Id,
                Lid = profile.Lid,
                Name = name,
                AvatarUrl = profile.AvatarUrl
            };
        }

        public async Task SyncCurrentProfileAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            string meId = _whatsAppService.CurrentProfile?.Id;
            if (string.IsNullOrWhiteSpace(meId))
            {
                Debug.WriteLine("[ProfileService] Sync skipped: Me.Id missing");
                return;
            }

            try
            {
                if (!_whatsAppService.IsTransportReady)
                {
                    await _whatsAppService.EnsureConnectedAsync();
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!_whatsAppService.IsTransportReady)
                {
                    Debug.WriteLine("[ProfileService] Sync skipped: socket not ready");
                    return;
                }

                string remoteUrl = await _whatsAppService.GetProfilePictureUrlAsync(meId, "preview");
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(remoteUrl))
                {
                    Debug.WriteLine("[ProfileService] Sync: no remote picture URL");
                    ApplyNameFromProfileIfNeeded();
                    return;
                }

                string localUri = null;
                try
                {
                    localUri = await _whatsAppService.CacheRemoteAvatarAsync(meId, remoteUrl, cancellationToken);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProfileService] Avatar cache failed: {ex.Message}");
                }

                string nextAvatar = !string.IsNullOrWhiteSpace(localUri) ? localUri : remoteUrl;
                if (!string.Equals(_whatsAppService.CurrentUserAvatar, nextAvatar, StringComparison.Ordinal))
                {
                    _whatsAppService.CurrentUserAvatar = nextAvatar;
                }

                ApplyNameFromProfileIfNeeded();
                Debug.WriteLine("[ProfileService] Sync completed");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProfileService] Sync failed: {ex.Message}");
            }
        }

        private void ApplyNameFromProfileIfNeeded()
        {
            if (!string.IsNullOrWhiteSpace(_whatsAppService.CurrentUserName))
            {
                return;
            }

            var profile = _whatsAppService.CurrentProfile;
            string name = profile?.Name;
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(profile?.Id))
            {
                name = profile.Id.Split('@')[0].Split(':')[0];
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                _whatsAppService.CurrentUserName = name;
            }
        }
    }
}
