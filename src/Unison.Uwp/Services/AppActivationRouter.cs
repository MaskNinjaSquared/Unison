using System;
using System.Diagnostics;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.ViewModels;
using Windows.Foundation;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// UWP adapter for <see cref="IAppActivationRouter"/>: parses People / launch arguments
    /// and queues chat or profile navigation on <see cref="ShellViewModel"/>.
    /// </summary>
    public sealed class AppActivationRouter : IAppActivationRouter
    {
        private readonly ShellViewModel _shell;
        private readonly IRuntimeDiagnostics _diagnostics;

        public AppActivationRouter(ShellViewModel shell, IRuntimeDiagnostics diagnostics)
        {
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            _diagnostics = diagnostics;
        }

        public void RouteLaunchArguments(string arguments, bool appWasAlreadyOpen)
        {
            if (string.IsNullOrWhiteSpace(arguments) || _shell == null)
            {
                return;
            }

            try
            {
                if (_shell.QueueOpenProfileFromActivation(arguments))
                {
                    return;
                }

                // Running/Suspended: list is (or was) already up — open the chat only.
                // NotRunning/Terminated/ClosedByUser: cold path may delay list until detail loads.
                bool delayChatList = ChatListDeepLinkSyncUi.ShouldArmListHold(appWasAlreadyOpen);
                _shell.QueueOpenChatFromActivation(arguments, delayChatList);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppActivationRouter] RouteLaunchArguments: " + ex.Message);
            }
        }

        public void RoutePeopleMessage(string remoteId, bool appWasAlreadyOpen)
        {
            string arguments = BuildPeopleLaunchArguments(remoteId, openProfile: false);
            WriteLifecycle(
                "people-message-activated",
                "hasJid=" + (arguments.Length > 0) + "; openProfile=False");
            RouteLaunchArguments(arguments, appWasAlreadyOpen);
        }

        public void RoutePeopleProfile(string remoteId, bool appWasAlreadyOpen)
        {
            string arguments = BuildPeopleLaunchArguments(remoteId, openProfile: true);
            WriteLifecycle(
                "people-profile-activated",
                "hasJid=" + (arguments.Length > 0) + "; openProfile=True");
            RouteLaunchArguments(arguments, appWasAlreadyOpen);
        }

        public void RouteContactPanel(string remoteId, bool appWasAlreadyOpen)
        {
            string arguments = BuildPeopleLaunchArguments(remoteId, openProfile: true);
            WriteLifecycle(
                "contact-panel-activated",
                "hasProfile=" + (arguments.Length > 0));
            RouteLaunchArguments(arguments, appWasAlreadyOpen);
        }

        public string BuildPeopleLaunchArguments(string remoteId, bool openProfile)
        {
            string jid = WindowsContactListPublisher.FromRemoteId(remoteId);
            if (string.IsNullOrWhiteSpace(jid))
            {
                return string.Empty;
            }

            string key = openProfile ? "profile" : "chat";
            return key + "=" + Uri.EscapeDataString(jid);
        }

        public string TryReadPeopleRemoteId(string uriQuery)
        {
            if (string.IsNullOrEmpty(uriQuery))
            {
                return null;
            }

            try
            {
                foreach (IWwwFormUrlDecoderEntry entry in new WwwFormUrlDecoder(uriQuery))
                {
                    if (string.Equals(entry.Name, "ContactRemoteIds", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(entry.Value))
                    {
                        return entry.Value
                            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)[0]
                            .Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppActivationRouter] TryReadPeopleRemoteId: " + ex.Message);
            }

            return null;
        }

        private void WriteLifecycle(string eventName, string detail)
        {
            try
            {
                _diagnostics?.Write("lifecycle", eventName, detail);
            }
            catch
            {
            }
        }
    }
}
