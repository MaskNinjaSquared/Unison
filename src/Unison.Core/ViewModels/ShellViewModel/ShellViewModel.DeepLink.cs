using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public partial class ShellViewModel
    {
        public bool QueueOpenProfileFromActivation(string arguments)
        {
            string jid = LaunchActivationHelper.TryGetProfileJid(arguments);
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            QueueOpenProfile(jid);
            return true;
        }

        /// <summary>Opens the full-page Profile for a contact JID (chat list / deep link).</summary>
        public void QueueOpenProfile(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            _pendingOpenProfileJid = jid.Trim();
            _lastQueuedProfileJid = _pendingOpenProfileJid;
            ClearPendingOpenChatJid();

            if (string.Equals(AppSurface, SurfaceConnected, StringComparison.Ordinal))
            {
                try
                {
                    NavigateToSection(NavigationRoutes.Profile);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ShellViewModel] Navigate profile failed: " + ex.Message);
                }
            }

            OnPropertyChanged(nameof(PendingOpenProfileJid));
            Debug.WriteLine("[ShellViewModel] Queued open profile: " + _pendingOpenProfileJid);
        }

        public void ClearPendingOpenProfileJid()
        {
            if (_pendingOpenProfileJid == null)
            {
                return;
            }

            _pendingOpenProfileJid = null;
            OnPropertyChanged(nameof(PendingOpenProfileJid));
        }

        /// <summary>
        /// Parses activation arguments and queues navigation into the target chat
        /// (narrow → detail pane when open succeeds). Used by secondary tiles and toast clicks.
        /// </summary>
        /// <param name="delayChatListUntilConversationReady">
        /// Cold start only. When the app was already open (list loaded), leave the list alone.
        /// </param>
        public void QueueOpenChatFromActivation(
            string arguments,
            bool delayChatListUntilConversationReady = true)
        {
            string jid = LaunchActivationHelper.TryGetChatJid(arguments);
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            ClearPendingOpenProfileJid();
            _lastQueuedProfileJid = null;

            // Prefer navigating onto Chats first so ChatsView is hooked before PropertyChanged.
            if (string.Equals(AppSurface, SurfaceConnected, StringComparison.Ordinal))
            {
                try
                {
                    NavigateToSection(NavigationRoutes.Chats);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ShellViewModel] Navigate chats for deep link failed: " + ex.Message);
                }
            }

            _pendingOpenChatJid = jid.Trim();
            bool armHold = _chatService.ShouldArmDeepLinkListHold(
                appWasAlreadyOpen: !delayChatListUntilConversationReady);
            if (armHold)
            {
                // Cold toast: keep chat-list x:Load held until the conversation finishes loading.
                _deepLinkHold?.Hold("activation:" + _pendingOpenChatJid);
            }
            else
            {
                Debug.WriteLine(
                    "[ShellViewModel] Deep-link list hold skipped (app already open): " +
                    _pendingOpenChatJid);
            }

            RaisePendingOpenChatChanged();
            Debug.WriteLine("[ShellViewModel] Queued open chat from activation: " + _pendingOpenChatJid);
        }

        public void ClearPendingOpenChatJid()
        {
            if (_pendingOpenChatJid == null)
            {
                return;
            }

            _pendingOpenChatJid = null;
            RaisePendingOpenChatChanged();
        }

        /// <summary>
        /// After toast deep-link conversation load (or give-up): allow the chat list to materialize.
        /// </summary>
        public void ReleaseDeepLinkListHold(string reason)
        {
            _deepLinkHold?.Release(reason ?? "deep-link-release");
        }

        /// <summary>Opens or closes the shell navigation pane.</summary>
        public ICommand TogglePaneCommand { get; }

        /// <summary>Navigates the shell content frame. Parameter: a <see cref="Constants.NavigationRoutes"/> value.</summary>
        public ICommand NavigateToSectionCommand { get; }

        /// <summary>Opens settings directly on the profile (expanded on Unison, profile section on WhatsApp).</summary>
        public ICommand OpenProfileSettingsCommand { get; }

        /// <summary>
        /// Imgur-style: wire service events once. Safe to call repeatedly.
    }
}
