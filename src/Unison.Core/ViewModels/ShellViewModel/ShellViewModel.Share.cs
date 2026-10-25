using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Constants;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public partial class ShellViewModel
    {
        private bool _isSharePickerOpen;
        private bool _sharePickerCatalogHooked;
        private IncomingSharePayload _pendingShare;
        private IncomingSharePayload _shareAwaitingChatApply;
        private string _forwardSourceChatJid;
        private string _forwardMessageId;
        private ObservableCollection<ChatItem> _sharePickerCandidates =
            new ObservableCollection<ChatItem>();

        /// <summary>Share Target / Forward contact picker overlay visibility.</summary>
        public bool IsSharePickerOpen
        {
            get => _isSharePickerOpen;
            set
            {
                if (_isSharePickerOpen == value)
                {
                    return;
                }

                _isSharePickerOpen = value;
                OnPropertyChanged(nameof(IsSharePickerOpen));
                if (value)
                {
                    HookSharePickerCatalog();
                    RebuildSharePicker();
                }
                else
                {
                    UnhookSharePickerCatalog();
                    ClearForwardPickerState();
                }
            }
        }

        public ObservableCollection<ChatItem> SharePickerCandidates
        {
            get => _sharePickerCandidates;
            private set
            {
                if (ReferenceEquals(_sharePickerCandidates, value))
                {
                    return;
                }

                _sharePickerCandidates = value;
                OnPropertyChanged(nameof(SharePickerCandidates));
            }
        }

        /// <summary>
        /// Share Target: "Send to…". Forward: localized Forward / Encaminhar
        /// (<c>ChatDetail_ForwardMessage</c>).
        /// </summary>
        public string SharePickerTitle =>
            IsForwardPickerOpen
                ? (_strings?.Get("ChatDetail_ForwardMessage", "Forward") ?? "Forward")
                : (_strings?.Get("Share_PickChat.Text", "Send to…") ?? "Send to…");

        /// <summary>Message id held while the Forward people picker is open; null otherwise.</summary>
        public string ForwardingMessageId => _forwardMessageId;

        /// <summary>Source chat JID held with <see cref="ForwardingMessageId"/>.</summary>
        public string ForwardingSourceChatJid => _forwardSourceChatJid;

        /// <summary>True when the people overlay is open for Forward (not Share Target).</summary>
        public bool IsForwardPickerOpen =>
            !string.IsNullOrWhiteSpace(_forwardMessageId) &&
            !string.IsNullOrWhiteSpace(_forwardSourceChatJid);

        /// <summary>Payload waiting for the user to pick a chat (picker open).</summary>
        public IncomingSharePayload PendingShare => _pendingShare;

        public ICommand CloseSharePickerCommand { get; private set; }

        public ICommand SelectShareChatCommand { get; private set; }

        private void InitShareCommands()
        {
            CloseSharePickerCommand = new RelayCommand(CloseSharePicker);
            SelectShareChatCommand = new RelayCommand<ChatItem>(SelectShareChat);
        }

        /// <inheritdoc />
        public void OpenForwardPicker(string sourceChatJid, string messageId)
        {
            Debug.WriteLine(
                "[Forward] OpenForwardPicker enter; source=" + (sourceChatJid ?? "(null)") +
                "; messageId=" + (messageId ?? "(null)") +
                "; wasOpen=" + _isSharePickerOpen +
                "; surface=" + AppSurface);

            if (string.IsNullOrWhiteSpace(sourceChatJid) || string.IsNullOrWhiteSpace(messageId))
            {
                Debug.WriteLine("[Forward] OpenForwardPicker aborted: empty source/messageId");
                return;
            }

            _pendingShare = null;
            _shareAwaitingChatApply = null;
            OnPropertyChanged(nameof(PendingShare));

            _forwardSourceChatJid = sourceChatJid.Trim();
            _forwardMessageId = messageId.Trim();
            OnPropertyChanged(nameof(ForwardingSourceChatJid));
            OnPropertyChanged(nameof(ForwardingMessageId));
            OnPropertyChanged(nameof(IsForwardPickerOpen));
            OnPropertyChanged(nameof(SharePickerTitle));

            OpenSharePicker();
            Debug.WriteLine(
                "[Forward] OpenForwardPicker done; source=" + _forwardSourceChatJid +
                "; messageId=" + _forwardMessageId +
                "; IsSharePickerOpen=" + _isSharePickerOpen +
                "; title=" + SharePickerTitle +
                "; candidates=" + SharePickerCandidates.Count);
        }

        private void ClearForwardPickerState()
        {
            if (string.IsNullOrWhiteSpace(_forwardMessageId) &&
                string.IsNullOrWhiteSpace(_forwardSourceChatJid))
            {
                return;
            }

            _forwardSourceChatJid = null;
            _forwardMessageId = null;
            OnPropertyChanged(nameof(ForwardingSourceChatJid));
            OnPropertyChanged(nameof(ForwardingMessageId));
            OnPropertyChanged(nameof(IsForwardPickerOpen));
            OnPropertyChanged(nameof(SharePickerTitle));
        }

        /// <summary>
        /// Opens the shell share picker with a materialized payload. Called from
        /// <see cref="Contracts.IAppActivationRouter.RouteShare"/>.
        /// </summary>
        /// <remarks>
        /// Cold Share Target often queues while Boot/Login is still up — do not open
        /// the overlay yet (no host XAML, and <see cref="_chatState"/> is still empty).
        /// <see cref="EnsurePendingSharePickerVisible"/> opens once Connected shell mounts.
        /// </remarks>
        public void QueueIncomingShare(IncomingSharePayload payload)
        {
            if (payload == null || payload.IsEmpty)
            {
                return;
            }

            ClearForwardPickerState();
            _pendingShare = payload;
            _shareAwaitingChatApply = null;
            OnPropertyChanged(nameof(PendingShare));
            OnPropertyChanged(nameof(SharePickerTitle));

            if (!string.Equals(AppSurface, SurfaceConnected, StringComparison.Ordinal))
            {
                Debug.WriteLine(
                    "[ShellViewModel] Share queued until Connected; hasText=" + payload.HasText +
                    "; media=" + (payload.Media?.Count ?? 0));
                return;
            }

            try
            {
                NavigateToSection(NavigationRoutes.Chats);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellViewModel] Navigate chats for share failed: " + ex.Message);
            }

            OpenSharePicker();
            Debug.WriteLine(
                "[ShellViewModel] Share picker opened; hasText=" + payload.HasText +
                "; media=" + (payload.Media?.Count ?? 0) +
                "; candidates=" + SharePickerCandidates.Count);
        }

        public void CloseSharePicker()
        {
            bool hadSharePayload = _pendingShare != null;
            IsSharePickerOpen = false;
            _pendingShare = null;
            OnPropertyChanged(nameof(PendingShare));
            ClearForwardPickerState();
            // Cancel = Complete (WinRT success path), not Fail — dismisses Mobile share host.
            if (hadSharePayload)
            {
                _shareTarget?.Complete();
            }
        }

        /// <summary>
        /// After chat detail applied the pending payload. Completes the Share Target
        /// contract when media was already sent; text-only keeps the host alive so the
        /// user can edit/send from the composer (ReportCompleted would kill that window).
        /// </summary>
        public void NotifyShareContentApplied(IncomingSharePayload payload)
        {
            if (payload == null || payload.IsEmpty)
            {
                return;
            }

            if (payload.HasMedia)
            {
                _shareTarget?.Complete();
            }
        }

        /// <summary>
        /// Cold share handoff can queue the payload while Boot/Login is still up; call when
        /// the connected shell is finally showing so the picker overlay has a host.
        /// Always rebuilds candidates — chats may have landed after a prior empty open.
        /// </summary>
        public void EnsurePendingSharePickerVisible()
        {
            if (_pendingShare == null || _pendingShare.IsEmpty)
            {
                return;
            }

            if (string.Equals(AppSurface, SurfaceConnected, StringComparison.Ordinal))
            {
                try
                {
                    NavigateToSection(NavigationRoutes.Chats);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ShellViewModel] Ensure share chats nav failed: " + ex.Message);
                }
            }

            OpenSharePicker();
        }

        /// <summary>
        /// After the conversation finishes loading, Chat detail takes this once (or null).
        /// </summary>
        public IncomingSharePayload TakeShareAwaitingChatApply()
        {
            IncomingSharePayload payload = _shareAwaitingChatApply;
            _shareAwaitingChatApply = null;
            return payload;
        }

        private void OpenSharePicker()
        {
            Debug.WriteLine(
                "[Forward/Share] OpenSharePicker; alreadyOpen=" + _isSharePickerOpen +
                "; forward=" + IsForwardPickerOpen);
            if (!_isSharePickerOpen)
            {
                IsSharePickerOpen = true;
                Debug.WriteLine(
                    "[Forward/Share] IsSharePickerOpen set true; now=" + _isSharePickerOpen);
                return;
            }

            // Already open (Boot flagged early, or Ensure after Queue): refresh rows.
            HookSharePickerCatalog();
            RebuildSharePicker();
        }

        private void SelectShareChat(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            if (IsForwardPickerOpen)
            {
                string sourceJid = _forwardSourceChatJid;
                string messageId = _forwardMessageId;
                string targetJid = chat.JID.Trim();
                IsSharePickerOpen = false;
                _ = ForwardSelectedAsync(sourceJid, messageId, targetJid);
                return;
            }

            IncomingSharePayload payload = _pendingShare;
            if (payload == null || payload.IsEmpty)
            {
                CloseSharePicker();
                return;
            }

            _shareAwaitingChatApply = payload;
            _pendingShare = null;
            OnPropertyChanged(nameof(PendingShare));
            IsSharePickerOpen = false;

            string arguments = "chat=" + Uri.EscapeDataString(chat.JID.Trim());
            QueueOpenChatFromActivation(arguments, delayChatListUntilConversationReady: false);
        }

        private async Task ForwardSelectedAsync(string sourceChatJid, string messageId, string targetJid)
        {
            if (_messageService == null)
            {
                Debug.WriteLine("[Forward] SelectShareChat aborted: IMessageService is NULL");
                return;
            }

            try
            {
                Debug.WriteLine(
                    "[Forward] SelectShareChat → ForwardMessageAsync source=" + sourceChatJid +
                    "; id=" + messageId +
                    "; to=" + targetJid);
                await _messageService.ForwardMessageAsync(sourceChatJid, messageId, targetJid)
                    .ConfigureAwait(false);

                // Open the destination so the user sees the forwarded bubble (same deep-link path as Share).
                await OpenChatAfterForwardAsync(targetJid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Forward] ForwardMessageAsync failed: " + ex.Message);
            }
        }

        private Task OpenChatAfterForwardAsync(string targetJid)
        {
            if (string.IsNullOrWhiteSpace(targetJid))
            {
                return Task.CompletedTask;
            }

            string arguments = "chat=" + Uri.EscapeDataString(targetJid.Trim());
            Action open = () =>
                QueueOpenChatFromActivation(arguments, delayChatListUntilConversationReady: false);

            if (_dispatcher != null)
            {
                return _dispatcher.RunAsync(open);
            }

            open();
            return Task.CompletedTask;
        }

        private void HookSharePickerCatalog()
        {
            if (_sharePickerCatalogHooked)
            {
                return;
            }

            _chatState.Chats.CollectionChanged += OnSharePickerChatsChanged;
            _chatState.DisplayNamesChanged += OnSharePickerDisplayNamesChanged;
            _sharePickerCatalogHooked = true;
        }

        private void UnhookSharePickerCatalog()
        {
            if (!_sharePickerCatalogHooked)
            {
                return;
            }

            _chatState.Chats.CollectionChanged -= OnSharePickerChatsChanged;
            _chatState.DisplayNamesChanged -= OnSharePickerDisplayNamesChanged;
            _sharePickerCatalogHooked = false;
        }

        private void OnSharePickerChatsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!SharePickerCatalogRefresh.ShouldRebuild(_isSharePickerOpen, catalogChanged: true))
            {
                return;
            }

            RebuildSharePicker();
        }

        private void OnSharePickerDisplayNamesChanged(object sender, EventArgs e)
        {
            if (!SharePickerCatalogRefresh.ShouldRebuild(_isSharePickerOpen, catalogChanged: true))
            {
                return;
            }

            RebuildSharePicker();
        }

        private void RebuildSharePicker()
        {
            SharePickerCandidates = new ObservableCollection<ChatItem>(
                SharePickerCatalog.BuildOrdered(
                    _chatState.Chats,
                    c => c.GetNameResolved(_strings) ?? c.Name ?? c.JID ?? string.Empty));
        }
    }
}
