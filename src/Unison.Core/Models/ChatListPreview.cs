using System.Collections.Generic;
using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    /// <summary>
    /// Chat-list last-message strip: author + kind chip + body + outgoing ticks.
    /// Bound as one object into <c>ChatListPreviewStrip</c>; not a timeline <see cref="ChatMessage"/>.
    /// </summary>
    public sealed class ChatListPreview : Observable
    {
        private string _text = string.Empty;
        private string _author = string.Empty;
        private ChatPreviewKind _kind;
        private bool _isFromMe;
        private MessageSendState _sendState;
        private List<string> _mentionedJids;

        public string Text
        {
            get => _text;
            set => Set(ref _text, value ?? string.Empty);
        }

        /// <summary>Group prefix ("Alice: "), empty for 1:1.</summary>
        public string Author
        {
            get => _author;
            set => Set(ref _author, value ?? string.Empty);
        }

        public ChatPreviewKind Kind
        {
            get => _kind;
            set
            {
                if (!Set(ref _kind, value))
                {
                    return;
                }

                RaiseProperties(
                    nameof(IsImage),
                    nameof(IsVideo),
                    nameof(IsSticker),
                    nameof(IsVoice),
                    nameof(IsText));
            }
        }

        public bool IsFromMe
        {
            get => _isFromMe;
            set
            {
                if (!Set(ref _isFromMe, value))
                {
                    return;
                }

                RaiseStatusUi();
            }
        }

        public MessageSendState SendState
        {
            get => _sendState;
            set
            {
                if (!Set(ref _sendState, value))
                {
                    return;
                }

                RaiseStatusUi();
            }
        }

        public List<string> MentionedJids
        {
            get => _mentionedJids;
            set => Set(ref _mentionedJids, value);
        }

        public bool IsImage => _kind == ChatPreviewKind.Image;
        public bool IsVideo => _kind == ChatPreviewKind.Video;
        public bool IsSticker => _kind == ChatPreviewKind.Sticker;
        public bool IsVoice => _kind == ChatPreviewKind.Voice;
        public bool IsText => _kind == ChatPreviewKind.Text;

        public bool ShowStatusCheckmark =>
            _isFromMe &&
            _sendState != MessageSendState.NotApplicable &&
            _sendState != MessageSendState.Failed &&
            !string.IsNullOrEmpty(StatusCheckmarkUri);

        public bool ShowSendFailed => _isFromMe && _sendState == MessageSendState.Failed;

        public string StatusCheckmarkUri
        {
            get
            {
                if (!_isFromMe || _sendState == MessageSendState.NotApplicable ||
                    _sendState == MessageSendState.Failed)
                {
                    return null;
                }

                switch (_sendState)
                {
                    case MessageSendState.Read:
                        return "ms-appx:///Assets/Checkmarks/read_message.png";
                    case MessageSendState.Delivered:
                        return "ms-appx:///Assets/Checkmarks/delivered_message.png";
                    default:
                        return "ms-appx:///Assets/Checkmarks/sent_message.png";
                }
            }
        }

        public void CopyFrom(ChatListPreview source)
        {
            if (source == null || ReferenceEquals(source, this))
            {
                return;
            }

            Text = source.Text;
            Author = source.Author;
            Kind = source.Kind;
            IsFromMe = source.IsFromMe;
            SendState = source.SendState;
            MentionedJids = source.MentionedJids != null && source.MentionedJids.Count > 0
                ? new List<string>(source.MentionedJids)
                : null;
        }

        private void RaiseStatusUi()
        {
            RaiseProperties(nameof(ShowStatusCheckmark), nameof(ShowSendFailed), nameof(StatusCheckmarkUri));
        }
    }
}
