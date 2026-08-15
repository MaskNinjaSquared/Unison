using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Message facade: Person upsert, domain <see cref="ChatMessage"/> construction, then WA transport.
    /// Prefer growing APIs here (e.g. <see cref="GetChatMessage"/>) instead of in WhatsAppService.
    /// </summary>
    public sealed class MessageService : IMessageService
    {
        private readonly IPersonStore _personStore;
        private readonly IWhatsAppService _whatsAppService;
        private readonly IChatMessageMapper _chatMessageMapper;
        private readonly IReactionMapper _reactionMapper;

        public MessageService(
            IPersonStore personStore,
            IWhatsAppService whatsAppService,
            IChatMessageMapper chatMessageMapper,
            IReactionMapper reactionMapper)
        {
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
            _chatMessageMapper = chatMessageMapper ?? throw new ArgumentNullException(nameof(chatMessageMapper));
            _reactionMapper = reactionMapper ?? throw new ArgumentNullException(nameof(reactionMapper));
        }

        public async Task SyncMessageHistoryAsync(HistorySync sync)
        {
            if (sync == null)
            {
                return;
            }

            try
            {
                await UpsertPeopleFromHistoryAsync(sync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageService] Person upsert from history failed: " + ex.Message);
            }

            await _whatsAppService.ProcessHistorySyncCoreAsync(sync).ConfigureAwait(false);
        }

        public Task<ChatMessage> SendTextMessageAsync(string jid, string text)
        {
            return _whatsAppService.SendTextMessageAsync(jid, text);
        }

        public Task SendImageAsync(string jid, byte[] imageBytes, string caption)
        {
            return _whatsAppService.SendImageAsync(jid, imageBytes, caption);
        }

        public Task<ChatMessage> SendAudioMessageAsync(string jid, byte[] audioBytes, string mimeType, uint durationSeconds, bool isVoiceMessage = false)
        {
            return _whatsAppService.SendAudioMessageAsync(jid, audioBytes, mimeType, durationSeconds, isVoiceMessage);
        }

        public Task<string> EnsureAudioAvailableAsync(ChatMessage message)
        {
            return _whatsAppService.EnsureAudioAvailableAsync(message);
        }

        public Task<string> EnsureImageAvailableAsync(ChatMessage message)
        {
            return _whatsAppService.EnsureImageAvailableAsync(message);
        }

        public Task<string> EnsureVideoAvailableAsync(ChatMessage message)
        {
            return _whatsAppService.EnsureVideoAvailableAsync(message);
        }

        public Task<string> EnsureDocumentAvailableAsync(ChatMessage message)
        {
            return _whatsAppService.EnsureDocumentAvailableAsync(message);
        }

        public Task SetMessagePinnedAsync(string chatJid, ChatMessage message, bool pin, uint durationSeconds = 604800)
        {
            return _whatsAppService.SetMessagePinnedAsync(chatJid, message, pin, durationSeconds);
        }

        public bool TryHandleReaction(
            Message message,
            ChatMessageMapContext context,
            IList<ChatMessage> chatMessages,
            out ChatMessage updatedParent)
        {
            updatedParent = null;
            PendingReaction pending;
            if (!_chatMessageMapper.TryMapReaction(message, context, out pending) || pending == null)
            {
                return false;
            }

            if (chatMessages != null)
            {
                _reactionMapper.TryApply(chatMessages, pending, out updatedParent);
            }

            return true;
        }

        public bool TryBufferReaction(
            Message message,
            ChatMessageMapContext context,
            out PendingReaction pending)
        {
            return _chatMessageMapper.TryMapReaction(message, context, out pending);
        }

        public ChatMessage GetChatMessage(ChatMessageMapContext context, ChatMessageContentSnapshot content)
        {
            return _chatMessageMapper.MapIndividual(context, content);
        }

        public void AttachHistoryReactions(
            ChatMessage parent,
            IEnumerable<Reaction> reactions,
            ChatMessageMapContext parentContext)
        {
            if (parent == null || reactions == null || parentContext == null)
            {
                return;
            }

            foreach (var reaction in reactions)
            {
                var pending = _reactionMapper.MapFromHistoryReaction(reaction, parentContext);
                if (pending != null)
                {
                    _reactionMapper.ApplyToMessage(parent, pending);
                }
            }
        }

        public IList<ChatMessage> ApplyBufferedReactions(
            IList<ChatMessage> chatMessages,
            IEnumerable<PendingReaction> pending)
        {
            return _reactionMapper.Apply(chatMessages, pending);
        }

        /// <summary>
        /// Inserts/updates Person rows. UpsertIfChanged skips writes when nothing changed.
        /// </summary>
        private async Task UpsertPeopleFromHistoryAsync(HistorySync sync)
        {
            await _personStore.InitializeAsync().ConfigureAwait(false);

            int writes = 0;

            if (sync.Pushnames != null)
            {
                foreach (var pn in sync.Pushnames)
                {
                    if (string.IsNullOrWhiteSpace(pn?.Id) || string.IsNullOrWhiteSpace(pn.Pushname_))
                    {
                        continue;
                    }

                    string jid = JidHelper.Normalize(pn.Id);
                    string phone = JidHelper.TryPhoneFromJid(jid);
                    if (await _personStore.UpsertIfChangedAsync(jid, pn.Pushname_, null, phone).ConfigureAwait(false))
                    {
                        writes++;
                    }
                }
            }

            if (sync.Conversations != null)
            {
                foreach (var conv in sync.Conversations)
                {
                    if (string.IsNullOrWhiteSpace(conv?.Id))
                    {
                        continue;
                    }

                    string jid = JidHelper.Normalize(conv.Id);
                    if (string.IsNullOrEmpty(jid) || JidHelper.IsGroupJid(jid))
                    {
                        continue;
                    }

                    string name = !string.IsNullOrWhiteSpace(conv.Name)
                        ? conv.Name
                        : conv.DisplayName;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string phone = JidHelper.TryPhoneFromJid(jid);
                    if (await _personStore.UpsertIfChangedAsync(jid, name, null, phone).ConfigureAwait(false))
                    {
                        writes++;
                    }
                }
            }

            Debug.WriteLine("[MessageService] Person upserts from history: " + writes);
        }

        public Task ResyncConversationsAsync(System.IProgress<ConversationResyncPhase> progress = null)
        {
            return _whatsAppService.ResyncConversationsAsync(progress);
        }

        public void StartNewChat(string jid)
        {
            _whatsAppService.StartNewChat(jid);
        }
    }
}
