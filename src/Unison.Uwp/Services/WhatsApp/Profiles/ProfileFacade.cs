// =============================================================================
// ProfileFacade
//
// The IProfileService implementation built on the Unison.Socket rewrite, and the
// template for every facade that follows.
//
// The split it demonstrates: the UseCase talks to the wire and returns raw data
// (a picture URL); the facade turns that into the app's world - downloading and
// caching the avatar, filling in a display name, persisting the result. Protocol
// knowledge stays portable in Unison.Socket, platform knowledge stays here.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Socket.UseCases.Profile;
using Unison.Uwp.Services.Socket;

namespace Unison.Uwp.Services.WhatsApp.Profiles
{
    public sealed class ProfileFacade : IProfileService
    {
        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IWhatsAppService _appState;

        /// <param name="appState">
        /// Still the owner of the cached avatar and the persisted profile. That half moves to
        /// the state store in a later phase; this facade only replaces the wire path.
        /// </param>
        internal ProfileFacade(IWhatsAppSessionProvider sessions, IWhatsAppService appState)
        {
            if (sessions == null)
            {
                throw new ArgumentNullException(nameof(sessions));
            }

            if (appState == null)
            {
                throw new ArgumentNullException(nameof(appState));
            }

            _sessions = sessions;
            _appState = appState;

            // Both live as long as the app does, so there is nothing to unhook from.
            _appState.OnUserProfileChanged += (s, e) =>
            {
                try
                {
                    ProfileChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ProfileFacade] ProfileChanged handler failed: " + ex.Message);
                }
            };

            // The shell asks for the profile while the socket is still handshaking, and a query
            // sent then is answered by nobody. This is the second chance: once the session is
            // actually up, ask again. Without it a cold start keeps showing whatever avatar was
            // cached on the previous run, or none at all on a fresh install.
            _appState.OnSessionInitialized += (s, e) => { _ = ResyncQuietlyAsync(); };
        }

        private async Task ResyncQuietlyAsync()
        {
            try
            {
                await SyncCurrentProfileAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ProfileFacade] Post-connect sync failed: " + ex.Message);
            }
        }

        public event EventHandler ProfileChanged;

        public Profile GetCurrentProfile()
        {
            var profile = _appState.CurrentProfile;
            if (profile == null)
            {
                return new Profile();
            }

            return new Profile
            {
                Id = profile.Id,
                Lid = profile.Lid,
                Name = ResolveDisplayName(profile),
                Phone = string.IsNullOrWhiteSpace(profile.Phone) ? null : profile.Phone.Trim(),
                AvatarUrl = profile.AvatarUrl
            };
        }

        public async Task SyncCurrentProfileAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var meId = _appState.CurrentProfile != null ? _appState.CurrentProfile.Id : null;
            if (string.IsNullOrWhiteSpace(meId))
            {
                Debug.WriteLine("[ProfileFacade] Sync skipped: own JID unknown");
                return;
            }

            // The shell starts this alongside the connection, not after it, so on a cold start
            // the socket is usually still handshaking when we get here. Giving up at that point
            // is why an updated avatar never arrived: nothing asks again once the session opens,
            // so the app kept showing whatever was cached from the last run.
            if (!_appState.IsTransportReady)
            {
                try
                {
                    await _appState.EnsureConnectedAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ProfileFacade] Sync skipped: could not reach a connection - " + ex.Message);
                    return;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            var session = _sessions.Current;
            if (session == null || !_sessions.IsReady)
            {
                Debug.WriteLine("[ProfileFacade] Sync skipped: the new socket stack is not connected");
                return;
            }

            try
            {
                var useCase = new FetchProfilePictureUrlUseCase(session.Connection);
                var picture = await useCase.ExecuteAsync(meId).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                if (!picture.HasUrl)
                {
                    // No picture is a normal state, not a failure worth retrying.
                    Debug.WriteLine(picture.IsNotFound
                        ? "[ProfileFacade] No profile picture set"
                        : "[ProfileFacade] Picture query refused: " + picture.FailureReason);

                    ApplyNameIfMissing();
                    return;
                }

                await ApplyAvatarAsync(meId, picture.Url, cancellationToken).ConfigureAwait(false);
                ApplyNameIfMissing();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ProfileFacade] Sync failed: " + ex.Message);
            }
        }

        private async Task ApplyAvatarAsync(string meId, string remoteUrl, CancellationToken cancellationToken)
        {
            string localUri = null;
            try
            {
                localUri = await _appState.CacheRemoteAvatarAsync(meId, remoteUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failed cache still leaves us with a usable remote URL.
                Debug.WriteLine("[ProfileFacade] Avatar cache failed: " + ex.Message);
            }

            var next = !string.IsNullOrWhiteSpace(localUri) ? localUri : remoteUrl;
            if (!string.Equals(_appState.CurrentUserAvatar, next, StringComparison.Ordinal))
            {
                _appState.CurrentUserAvatar = next;
            }
        }

        private void ApplyNameIfMissing()
        {
            if (!string.IsNullOrWhiteSpace(_appState.CurrentUserName))
            {
                return;
            }

            var name = ResolveDisplayName(_appState.CurrentProfile);
            if (!string.IsNullOrWhiteSpace(name))
            {
                _appState.CurrentUserName = name;
            }
        }

        /// <summary>The account's push name, or null until one arrives — never the JID digits.</summary>
        private static string ResolveDisplayName(Profile profile)
        {
            if (profile == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                return null;
            }

            string name = profile.Name.Trim();
            if (string.IsNullOrEmpty(profile.Id))
            {
                return name;
            }

            string idUser = profile.Id.Split('@')[0].Split(':')[0];
            return string.Equals(name, idUser, StringComparison.OrdinalIgnoreCase)
                ? null
                : name;
        }
    }
}
