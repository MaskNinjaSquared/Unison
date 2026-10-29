using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Exceptions;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        // ── Lifecycle ─────────────────────────────────────────────────────────

        public Task InitializeAsync()
        {
            _participants.ParticipantAvatarChanged -= OnParticipantAvatarChanged;
            _participants.ParticipantAvatarChanged += OnParticipantAvatarChanged;
            Attach();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Leave the detail surface: stop elapsed UI, cancel mic, stop presence watch.
        /// Idempotent — call from view Unloaded / navigate-away.
        /// </summary>
        public async Task UninitializeAsync()
        {
            StopPresenceWatch();
            await CancelRecordingCoreAsync();
            _participants.ParticipantAvatarChanged -= OnParticipantAvatarChanged;
            _participants.DetachHooks();
            ClearTimeline();
            if (_contactService != null)
            {
                _contactService.DisplayNamesUpdated -= Contacts_DisplayNamesUpdated;
            }

            _attached = false;
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            if (_contactService != null)
            {
                _contactService.DisplayNamesUpdated += Contacts_DisplayNamesUpdated;
            }

            // Live merge into Messages is owned by ChatDetailView.SyncMessagesFromServiceAsync
            // until that path is fully moved here (avoids double-append on the same collection).
        }

        /// <summary>Wrap a domain message for the timeline (view load/sync paths).</summary>
        public ChatMessageViewModel CreateMessageVm(ChatMessage message)
        {
            message?.EnsureKindFromLegacyFlags();
            var vm = _messageFactory.Create(message);
            if (vm != null)
            {
                vm.PinnedChanged -= OnBubblePinnedChanged;
                vm.PinnedChanged += OnBubblePinnedChanged;
                vm.ReplyRequested -= OnBubbleReplyRequested;
                vm.ReplyRequested += OnBubbleReplyRequested;
                vm.ForwardRequested -= OnBubbleForwardRequested;
                vm.ForwardRequested += OnBubbleForwardRequested;
                vm.DeletedForMe -= OnBubbleDeletedForMe;
                vm.DeletedForMe += OnBubbleDeletedForMe;
                vm.AudioPlayPauseRequested -= OnBubbleAudioPlayPauseRequested;
                vm.AudioPlayPauseRequested += OnBubbleAudioPlayPauseRequested;
                vm.AudioCycleSpeedRequested -= OnBubbleAudioCycleSpeedRequested;
                vm.AudioCycleSpeedRequested += OnBubbleAudioCycleSpeedRequested;
                vm.AudioPlayAtSpeedRequested -= OnBubbleAudioPlayAtSpeedRequested;
                vm.AudioPlayAtSpeedRequested += OnBubbleAudioPlayAtSpeedRequested;
                vm.AttachMentionLookup(ActiveChat?.MentionLookup);
                vm.SetDeleteChatContext(ActiveChat != null && ActiveChat.IsGroupAdmin);
            }

            return vm;
        }

        /// <summary>Clears the timeline and detaches each bubble so models do not keep VMs alive.</summary>
        public void ClearTimeline()
        {
            for (int i = 0; i < Messages.Count; i++)
            {
                ReleaseMessageVm(Messages[i]);
            }

            Messages.Clear();
            IsDetachedFromEnd = false;
        }

        /// <summary>Removes one timeline row after <see cref="ChatMessageViewModel.Detach"/>.</summary>
        public void RemoveTimelineAt(int index)
        {
            if (index < 0 || index >= Messages.Count)
            {
                return;
            }

            ReleaseMessageVm(Messages[index]);
            Messages.RemoveAt(index);
        }

        /// <summary>Drops one bubble after delete-for-me (cache/SQL already updated).</summary>
        public bool TryRemoveTimelineMessage(ChatMessageViewModel message)
        {
            if (message == null)
            {
                return false;
            }

            int index = Messages.IndexOf(message);
            if (index < 0 && !string.IsNullOrWhiteSpace(message.Id))
            {
                for (int i = 0; i < Messages.Count; i++)
                {
                    if (string.Equals(Messages[i]?.Id, message.Id, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index < 0)
            {
                return false;
            }

            RemoveTimelineAt(index);
            return true;
        }

        /// <summary>Drops oldest rows until <paramref name="maxCount"/> remain (live append / stick-to-bottom).</summary>
        public void TrimTimelineToMax(int maxCount)
        {
            if (maxCount < 0)
            {
                maxCount = 0;
            }

            while (Messages.Count > maxCount)
            {
                RemoveTimelineAt(0);
            }
        }

        /// <summary>Drops newest rows until <paramref name="maxCount"/> remain (user scrolled up loading older).</summary>
        public void TrimTimelineNewestToMax(int maxCount)
        {
            if (maxCount < 0)
            {
                maxCount = 0;
            }

            while (Messages.Count > maxCount)
            {
                RemoveTimelineAt(Messages.Count - 1);
                IsDetachedFromEnd = true;
            }
        }

        private bool _isDetachedFromEnd;

        /// <summary>
        /// True after load-more trimmed the newest rows: the window no longer reaches the latest
        /// message, and only <see cref="ChatTimelineMergeMode.RejoinEnd"/> brings it back.
        /// </summary>
        public bool IsDetachedFromEnd
        {
            get => _isDetachedFromEnd;
            private set => Set(ref _isDetachedFromEnd, value);
        }

        /// <summary>Clears top-scroll paging flags when switching conversations.</summary>
        public void ResetTimelinePaging()
        {
            _sessionUnreadDividerCount = 0;
            _sessionUnreadIsChannel = false;
            _inboundHistoryReadCommitted = false;
            _hasReachedStart = false;
            _emptyLoadAttempts = 0;
            IsLoadingMore = false;
            IsLoadingMessages = false;
            OnPropertyChanged(nameof(CanLoadMore));
        }

        /// <summary>
        /// Replaces the UI window with factory-made VMs for <paramref name="messages"/> (already sliced by the caller).
        /// </summary>
        public void ReplaceTimelineWindow(IEnumerable<ChatMessage> messages)
        {
            ClearTimeline();
            if (messages == null || _messageFactory == null)
            {
                return;
            }

            foreach (var msg in messages)
            {
                if (msg == null)
                {
                    continue;
                }

                var vm = CreateMessageVm(msg);
                if (vm != null)
                {
                    Messages.Add(vm);
                }
            }
        }

        private void ReleaseMessageVm(ChatMessageViewModel vm)
        {
            if (vm == null)
            {
                return;
            }

            vm.PinnedChanged -= OnBubblePinnedChanged;
            vm.ReplyRequested -= OnBubbleReplyRequested;
            vm.ForwardRequested -= OnBubbleForwardRequested;
            vm.DeletedForMe -= OnBubbleDeletedForMe;
            vm.AudioPlayPauseRequested -= OnBubbleAudioPlayPauseRequested;
            vm.AudioCycleSpeedRequested -= OnBubbleAudioCycleSpeedRequested;
            vm.AudioPlayAtSpeedRequested -= OnBubbleAudioPlayAtSpeedRequested;
            vm.Detach();
        }

        private void OnBubblePinnedChanged(object sender, EventArgs e) =>
            MessagePinnedChanged?.Invoke(this, EventArgs.Empty);

        private void OnBubbleReplyRequested(object sender, EventArgs e)
        {
            BeginReply(sender as ChatMessageViewModel);
        }

        private void OnBubbleForwardRequested(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(
                "[Forward] OnBubbleForwardRequested id=" +
                ((sender as ChatMessageViewModel)?.Model?.Id ?? "(null)"));
            BeginForward(sender as ChatMessageViewModel);
        }

        private void OnBubbleDeletedForMe(object sender, EventArgs e)
        {
            TryRemoveTimelineMessage(sender as ChatMessageViewModel);
            if (ReferenceEquals(SelectedMessage, sender))
            {
                DismissMessageActions();
            }
        }

        private ChatMessageViewModel _composerReply;

        /// <summary>The bubble the composer is replying to. The same instance the timeline shows.</summary>
        public ChatMessageViewModel ComposerReply
        {
            get => _composerReply;
            private set
            {
                if (ReferenceEquals(_composerReply, value))
                {
                    return;
                }

                _composerReply = value;
                OnPropertyChanged(nameof(ComposerReply));
                OnPropertyChanged(nameof(IsReplyBarOpen));
            }
        }

        public bool IsReplyBarOpen => _composerReply != null;

        public ICommand DismissReplyCommand { get; }

        /// <summary>Opens the reply bar on this bubble and closes the Legacy action bar.</summary>
        public void BeginReply(ChatMessageViewModel message)
        {
            if (message == null || message.Model == null || string.IsNullOrWhiteSpace(message.Model.Id))
            {
                return;
            }

            ComposerReply = message;
            DismissMessageActions();
        }

        public void DismissReply()
        {
            ComposerReply = null;
        }

        /// <summary>
        /// Opens the shell people picker titled Forward, holding source chat + message id.
        /// Person click sends via <see cref="Contracts.WhatsApp.IMessageService.ForwardMessageAsync"/>.
        /// </summary>
        public void BeginForward(ChatMessageViewModel message)
        {
            if (message == null || message.Model == null || string.IsNullOrWhiteSpace(message.Model.Id))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Forward] BeginForward aborted: missing message/id");
                return;
            }

            string sourceJid = ActiveChat != null ? ActiveChat.JID : null;
            if (string.IsNullOrWhiteSpace(sourceJid))
            {
                sourceJid = message.Model.RemoteJid;
            }

            if (string.IsNullOrWhiteSpace(sourceJid))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Forward] BeginForward aborted: missing source chat JID");
                return;
            }

            if (_forwardPicker == null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Forward] BeginForward aborted: IForwardMessagePicker is NULL (DI)");
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Forward] BeginForward → OpenForwardPicker source=" + sourceJid +
                "; id=" + message.Model.Id);
            DismissMessageActions();
            _forwardPicker.OpenForwardPicker(sourceJid, message.Model.Id);
        }

    }
}
