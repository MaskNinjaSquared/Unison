// =============================================================================
// ChatsModule
//
// The account-level operations that are neither messages nor groups: presence,
// privacy, the blocklist and our own profile.
//
// Like groups, none of this needs routing - it is all request and response, and
// the changes that arrive unprompted come in as notifications the message layer
// already handles. Assembling it in one place keeps a host from having to know
// which use case lives where.
//
// Ports: rc14 makeChatsSocket in src/Socket/chats.ts, minus the app state half
// which is its own module
// =============================================================================
using System;
using Unison.Baileys.Client;
using Unison.Socket.Session;
using Unison.Socket.UseCases.Profile;

namespace Unison.Socket.UseCases.Chats
{
    public sealed class ChatsModule
    {
        /// <param name="meName">
        /// Our display name, which the server insists on before it accepts a presence. Defaults to
        /// the one on the credentials, which the phone fills in shortly after linking.
        /// </param>
        public ChatsModule(WhatsAppSession session, AuthState auth, Func<string> meName = null)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            var connection = session.Connection;

            Presence = new SendPresenceUseCase(
                connection,
                () => auth.Me != null ? auth.Me.Id : null,
                () => auth.Me != null ? auth.Me.Lid : null,
                meName ?? (() => auth.Me != null ? auth.Me.Name : null));

            Privacy = new PrivacySettingsUseCase(connection);
            Blocklist = new BlocklistUseCase(connection);
            Profile = new UpdateProfileUseCase(connection);
            ProfilePicture = new FetchProfilePictureUrlUseCase(connection);
        }

        /// <summary>Online status, typing indicators, and subscribing to other people's.</summary>
        public SendPresenceUseCase Presence { get; }

        public PrivacySettingsUseCase Privacy { get; }

        public BlocklistUseCase Blocklist { get; }

        /// <summary>Our own status line and picture, or a group's picture.</summary>
        public UpdateProfileUseCase Profile { get; }

        public FetchProfilePictureUrlUseCase ProfilePicture { get; }
    }
}
