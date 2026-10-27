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
        private void NavigateToSection(string section)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                return;
            }

            // Overlay hamburger: close as soon as a section is chosen (Settings/Debug/Chats).
            if (IsPhoneHandset() && _isPaneOpen)
            {
                IsPaneOpen = false;
            }

            try
            {
                // Navigate BEFORE ActiveSection so MainView.WireChatsViewMenu sees the live Frame.Content.
                // (Setting ActiveSection first wired the previous page, then ShellNavigated skipped
                // re-wire when the route string was already applied.)
                if (string.Equals(section, NavigationRoutes.Settings, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.OpenSettings();
                }
                else if (string.Equals(section, NavigationRoutes.Debug, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.NavigateInShell(NavigationDestination.Debug);
                }
                else if (string.Equals(section, NavigationRoutes.Status, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.NavigateInShellAndClear(NavigationDestination.Status);
                }
                else if (string.Equals(section, NavigationRoutes.Calls, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.NavigateInShellAndClear(NavigationDestination.Calls);
                }
                else if (string.Equals(section, NavigationRoutes.Favorites, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.NavigateInShellAndClear(NavigationDestination.Favorites);
                }
                else if (string.Equals(section, NavigationRoutes.Archived, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.NavigateInShellAndClear(NavigationDestination.Archived);
                }
                else if (string.Equals(section, NavigationRoutes.Channels, StringComparison.OrdinalIgnoreCase))
                {
                    _navigator.NavigateInShellAndClear(NavigationDestination.Channels);
                }
                else if (string.Equals(section, NavigationRoutes.Profile, StringComparison.OrdinalIgnoreCase))
                {
                    string profileJid = _pendingOpenProfileJid ?? _lastQueuedProfileJid;
                    _navigator.NavigateInShell(NavigationDestination.Profile, profileJid);
                }
                else
                {
                    // Legacy pivot shells throw here (chats live in the pivot). ActiveSection
                    // below still updates; Legacy ShellView dismisses the overlay on that change.
                    _navigator.NavigateInShellAndClear(NavigationDestination.Chats);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] NavigateInShell failed: " + ex.Message);
            }

            ActiveSection = section;
            RaiseSystemBackButtonChanged();
        }

        /// <summary>
        /// Hardware / system back for shell sections (Settings / Debug → previous shell page).
        /// Chat list/detail back is handled by ChatsView before this is called.
        /// </summary>
        public bool TryHandleShellBack()
        {
            // Profile is always opened from outside (People / profile=) — back is Home, not stack.
            if (string.Equals(ActiveSection, NavigationRoutes.Profile, StringComparison.OrdinalIgnoreCase))
            {
                NavigateToSection(NavigationRoutes.Chats);
                return true;
            }

            if (_navigator.CanGoBackInShell)
            {
                _navigator.GoBackInShell();
                return true;
            }

            if (string.Equals(ActiveSection, NavigationRoutes.Settings, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ActiveSection, NavigationRoutes.Debug, StringComparison.OrdinalIgnoreCase))
            {
                NavigateToSection(NavigationRoutes.Chats);
                return true;
            }

            return false;
        }

        public void ReportWindowNarrow(bool isNarrow)
        {
            // Handset (not Continuum): never switch to desktop dual-pane / Inline open
            // hamburger just because landscape width ≥ 720.
            if (IsPhoneHandset())
            {
                isNarrow = true;
            }

            bool changed = IsNarrowWindow != isNarrow;
            IsNarrowWindow = isNarrow;
            // Do not auto-close the hamburger here — layout thrash during sync /
            // SizeChanged closed the pane right after the user opened it.
            if (changed)
            {
                SyncChatPane();
            }
        }

        /// <summary>
        /// Call on display orientation change. Closes the overlay hamburger on Mobile
        /// handset; Continuum keeps desktop-like pane behaviour.
        /// </summary>
        public void OnDisplayOrientationChanged()
        {
            if (IsPhoneHandset())
            {
                // Re-assert narrow chat chrome after orientation (chat stays fullscreen).
                ReportWindowNarrow(true);
                CloseHamburgerIfPhoneHandset();
            }
        }

        private bool IsPhoneHandset()
        {
            return _systemInfo != null && _systemInfo.IsMobile() && !_systemInfo.IsContinuum();
        }

        private void CloseHamburgerIfPhoneHandset()
        {
            if (!IsPhoneHandset())
            {
                return;
            }

            if (_isPaneOpen)
            {
                IsPaneOpen = false;
            }
            else
            {
                // VSM Extended may open SplitView without changing the VM value —
                // force PropertyChanged so MainView closes the control.
                RaisePaneOpenChanged();
            }
        }

        public void SelectChat(ChatItem chat)
        {
            PendingChat = chat;
            HasActiveChat = chat != null;

            if (IsNarrowWindow)
            {
                ChatPane = chat != null ? PaneNarrowDetail : PaneNarrowList;
            }

            RaiseSystemBackButtonChanged();
        }

        public void ClearChat()
        {
            PendingChat = null;
            HasActiveChat = false;

            if (IsNarrowWindow)
            {
                ChatPane = PaneNarrowList;
            }

            RaiseSystemBackButtonChanged();
        }

        public void ReportActiveChat(bool hasActiveChat)
        {
            HasActiveChat = hasActiveChat;
            if (!hasActiveChat)
            {
                PendingChat = null;
            }

            if (IsNarrowWindow)
            {
                SyncChatPane();
            }
            else
            {
                RaiseSystemBackButtonChanged();
            }
        }

        public void SyncChatPane()
        {
            if (string.Equals(ActiveSection, NavigationRoutes.Status, StringComparison.OrdinalIgnoreCase))
            {
                SyncStatusPane();
                return;
            }

            if (IsNarrowWindow)
            {
                // Minimal: chat space only with an open intent (PendingChat) and active flag.
                // Do not call EnsureNarrowListWhenNoActiveChat before assigning — that path
                // cleared PendingChat while SelectChat was switching to NarrowDetail.
                bool showDetail = HasActiveChat && PendingChat != null;
                ChatPane = showDetail ? PaneNarrowDetail : PaneNarrowList;
            }
            else
            {
                ChatPane = PaneWideBoth;
            }

            RaiseSystemBackButtonChanged();
        }

        private void SyncStatusPane()
        {
            if (IsNarrowWindow)
            {
                ChatPane = _statusDetailOpen ? PaneNarrowDetail : PaneNarrowList;
            }
            else
            {
                ChatPane = PaneWideBoth;
            }

            RaiseSystemBackButtonChanged();
        }

        /// <summary>
        /// Minimal / narrow: if the chat space is showing but nothing is selected, force list.
        /// </summary>
        public void EnsureNarrowListWhenNoActiveChat()
        {
            if (!IsNarrowWindow)
            {
                return;
            }

            if (!string.Equals(ChatPane, PaneNarrowDetail, StringComparison.Ordinal))
            {
                return;
            }

            if (HasActiveChat && PendingChat != null)
            {
                return;
            }

            PendingChat = null;
            HasActiveChat = false;
            ChatPane = PaneNarrowList;
            RaiseSystemBackButtonChanged();
        }

        public void RefreshUserInfo()
        {
            ApplyProfile(_profileService.GetCurrentProfile());
        }

        private void ApplyProfile(Profile profile)
        {
            if (profile == null)
            {
                CurrentUserName = null;
                CurrentUserPhone = null;
                CurrentUserAvatar = null;
                return;
            }

            CurrentUserName = profile.Name;
            CurrentUserPhone = profile.Phone;
            CurrentUserAvatar = profile.AvatarUrl;
        }
    }
}
