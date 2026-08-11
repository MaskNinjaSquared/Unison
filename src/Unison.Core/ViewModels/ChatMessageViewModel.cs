using System;
using System.ComponentModel;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// One timeline bubble: wraps <see cref="ChatMessage"/> and owns presentation/commands
    /// (e.g. reactions button, read-more expand).
    /// </summary>
    public class ChatMessageViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Collapsed body line cap (UWP <c>TextBlock.MaxLines</c>).</summary>
        public const int CollapsedContentMaxLines = 12;

        private readonly IStringResources _strings;
        private bool _isExpanded;

        public ChatMessage Model { get; }

        public ChatMessageViewModel(ChatMessage model, IStringResources strings = null)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            _strings = strings;

            ShowReactionsCommand = new RelayCommand(
                () => { /* stub: open reactors list later */ },
                () => HasReactions);

            ToggleExpandCommand = new RelayCommand(
                () => IsExpanded = !IsExpanded,
                () => CanExpand);

            Model.PropertyChanged += (s, e) =>
            {
                PropertyChanged?.Invoke(this, e);

                if (e.PropertyName == nameof(ChatMessage.HasReactions) ||
                    e.PropertyName == nameof(ChatMessage.TotalReactions) ||
                    e.PropertyName == nameof(ChatMessage.ReactionsDisplayText) ||
                    e.PropertyName == nameof(ChatMessage.Reactions))
                {
                    (ShowReactionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }

                if (e.PropertyName == nameof(ChatMessage.Content) ||
                    e.PropertyName == nameof(ChatMessage.ShowTextContent) ||
                    e.PropertyName == nameof(ChatMessage.IsAudio) ||
                    e.PropertyName == nameof(ChatMessage.ImageUri) ||
                    e.PropertyName == nameof(ChatMessage.NeedsImageDownload) ||
                    e.PropertyName == nameof(ChatMessage.Kind))
                {
                    RaiseExpandPropsChanged();
                }

                if (e.PropertyName == nameof(ChatMessage.NeedsImageDownload) ||
                    e.PropertyName == nameof(ChatMessage.HasImage) ||
                    e.PropertyName == nameof(ChatMessage.ImageUri))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowImageDownloadIcon)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDownloadImage)));
                }
            };
        }

        public ICommand ShowReactionsCommand { get; }
        public ICommand ToggleExpandCommand { get; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                RaiseExpandPropsChanged();
            }
        }

        /// <summary>True when body text is long enough to warrant collapse / read-more.</summary>
        public bool CanExpand =>
            ShowTextContent && NeedsCollapse(Content, CollapsedContentMaxLines);

        /// <summary>0 = unlimited when expanded; otherwise <see cref="CollapsedContentMaxLines"/>.</summary>
        public int ContentMaxLines =>
            IsExpanded || !CanExpand ? 0 : CollapsedContentMaxLines;

        public string ExpandLinkText
        {
            get
            {
                if (IsExpanded)
                {
                    return _strings != null
                        ? _strings.Get("ChatDetail_ReadLess", "Read less")
                        : "Read less";
                }

                return _strings != null
                    ? _strings.Get("ChatDetail_ReadMore", "Read more")
                    : "Read more";
            }
        }

        public string Id => Model.Id;
        public string Content => Model.Content;
        public DateTime Timestamp => Model.Timestamp;
        public bool IsFromMe => Model.IsFromMe;
        public string Status
        {
            get => Model.Status;
            set => Model.Status = value;
        }

        public string StatusCheckmarkUri => Model.StatusCheckmarkUri;
        public bool HasStatusCheckmark => Model.HasStatusCheckmark;
        public bool IsSendFailed => Model.IsSendFailed;
        public string SenderName => Model.SenderName;
        public ChatMessageKind Kind => Model.Kind;
        public bool IsAudio => Model.IsAudio;

        public string AudioButtonText
        {
            get
            {
                string kind = Model.IsVoiceMessage
                    ? (_strings != null
                        ? _strings.Get("ChatList_PreviewVoice", "Voice message")
                        : "Voice message")
                    : (_strings != null
                        ? _strings.Get("ChatDetail_Audio", "Audio")
                        : "Audio");

                uint seconds = Model.AudioDurationSeconds;
                if (seconds == 0)
                {
                    return kind;
                }

                return string.Format("{0}  {1}:{2:00}", kind, seconds / 60, seconds % 60);
            }
        }

        public string ImageUri => Model.ImageUri;
        public bool HasImage => Model.HasImage;
        public bool NeedsImageDownload => Model.NeedsImageDownload;
        public bool CanDownloadImage => Model.CanDownloadImage;
        public string Caption => Model.Caption;
        public bool HasCaption => Model.HasCaption;
        public bool ShowTextContent => Model.ShowTextContent;

        private bool _isImageDownloading;
        public bool IsImageDownloading
        {
            get => _isImageDownloading;
            set
            {
                if (_isImageDownloading == value) return;
                _isImageDownloading = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImageDownloading)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowImageDownloadIcon)));
            }
        }

        /// <summary>Download glyph visible when pending and not mid-download.</summary>
        public bool ShowImageDownloadIcon => NeedsImageDownload && !IsImageDownloading;
        public string RemoteJid
        {
            get => Model.RemoteJid;
            set => Model.RemoteJid = value;
        }

        public string ParticipantJid
        {
            get => Model.ParticipantJid;
            set => Model.ParticipantJid = value;
        }

        public bool IsPinned
        {
            get => Model.IsPinned;
            set => Model.IsPinned = value;
        }

        public DateTime? PinnedAtUtc
        {
            get => Model.PinnedAtUtc;
            set => Model.PinnedAtUtc = value;
        }

        public DateTime? PinExpiresAtUtc
        {
            get => Model.PinExpiresAtUtc;
            set => Model.PinExpiresAtUtc = value;
        }

        public bool HasReactions => Model.HasReactions;
        public int TotalReactions => Model.TotalReactions;
        public string ReactionsDisplayText => Model.ReactionsDisplayText;

        public bool IsRunStart
        {
            get => Model.IsRunStart;
            set => Model.IsRunStart = value;
        }

        public bool IsRunEnd
        {
            get => Model.IsRunEnd;
            set => Model.IsRunEnd = value;
        }

        public bool ShowTail => Model.ShowTail;

        public bool ShowGroupSenderName
        {
            get => Model.ShowGroupSenderName;
            set => Model.ShowGroupSenderName = value;
        }

        public string FormattedTime => Model.FormattedTime;

        private void RaiseExpandPropsChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanExpand)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentMaxLines)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandLinkText)));
            (ToggleExpandCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Heuristic for SDK &lt; 16299 (no <c>IsTextTrimmed</c>): hard newlines or soft-wrap estimate.
        /// </summary>
        private static bool NeedsCollapse(string text, int maxLines)
        {
            if (string.IsNullOrEmpty(text) || maxLines <= 0)
            {
                return false;
            }

            int hardLines = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' || text[i] == '\r')
                {
                    if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    hardLines++;
                    if (hardLines > maxLines)
                    {
                        return true;
                    }
                }
            }

            // ~40 chars per wrapped line in a typical bubble width.
            return text.Length > maxLines * 40;
        }
    }
}
