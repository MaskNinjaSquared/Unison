using System;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Mappers;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Full-screen video viewer chrome: message metadata + placeholder Share/Download (disabled for now).
    /// Playback/SMTC live in the UWP view (Imgur FullScreenMediaView pattern).
    /// </summary>
    public sealed class VideoViewerViewModel : Observable
    {
        private readonly IStringResources _strings;
        private bool _isChromeVisible = true;

        public VideoViewerViewModel(ChatMessageViewModel message, IStringResources strings = null)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            _strings = strings;

            CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
            ToggleChromeCommand = new RelayCommand(() => IsChromeVisible = !IsChromeVisible);

            // Placeholders — wired but always disabled until export is implemented for video.
            ShareCommand = new RelayCommand(() => { }, () => false);
            DownloadCommand = new RelayCommand(() => { }, () => false);
        }

        public event EventHandler CloseRequested;

        public ChatMessageViewModel Message { get; }

        public ICommand CloseCommand { get; }
        public ICommand ToggleChromeCommand { get; }
        public ICommand ShareCommand { get; }
        public ICommand DownloadCommand { get; }

        public string VideoUri => Message?.Model?.VideoUri;

        public string VideoPosterUri => Message?.Model?.VideoPosterUri;

        public bool HasLocalVideo => Message?.Model?.HasLocalVideo == true;

        public string Caption => Message?.Caption;

        public bool HasCaption => Message?.HasCaption == true;

        public string SenderDisplayName
        {
            get
            {
                if (Message.IsFromMe)
                {
                    return _strings != null
                        ? _strings.Get("Chat_SelfFallbackName", "You")
                        : "You";
                }

                if (!string.IsNullOrWhiteSpace(Message.SenderName))
                {
                    return Message.SenderName.Trim();
                }

                return _strings != null
                    ? _strings.Get("ImageViewer_UnknownSender", "Contact")
                    : "Contact";
            }
        }

        public string FormattedDateTime
        {
            get
            {
                return WhatsAppMapper.FormatLocalDateTime(Message.Timestamp);
            }
        }

        public string DownloadTooltip =>
            _strings != null
                ? _strings.Get("ImageViewer_Download", "Download")
                : "Download";

        public string ShareTooltip =>
            _strings != null
                ? _strings.Get("ImageViewer_Share", "Share")
                : "Share";

        public bool IsChromeVisible
        {
            get => _isChromeVisible;
            set => Set(ref _isChromeVisible, value);
        }
    }
}
