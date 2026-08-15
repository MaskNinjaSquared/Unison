using System;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Helpers;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Full-screen image viewer: takes the bubble <see cref="ChatMessageViewModel"/>
    /// and exposes share / save-local commands.
    /// </summary>
    public sealed class ImageViewerViewModel : Observable
    {
        private readonly IShareService _share;
        private readonly IFilePicker _files;
        private readonly IStringResources _strings;
        private bool _isChromeVisible = true;
        private bool _isBusy;

        public ImageViewerViewModel(
            ChatMessageViewModel message,
            IShareService share,
            IFilePicker files,
            IStringResources strings)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            _share = share ?? throw new ArgumentNullException(nameof(share));
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _strings = strings;

            CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
            ToggleChromeCommand = new RelayCommand(() => IsChromeVisible = !IsChromeVisible);
            ShareCommand = new RelayCommand(
                async () => await ShareAsync(),
                () => CanExport);
            DownloadCommand = new RelayCommand(
                async () => await DownloadAsync(),
                () => CanExport && !IsBusy);
        }

        public event EventHandler CloseRequested;

        public ChatMessageViewModel Message { get; }

        /// <summary>Closes the fullscreen image viewer overlay.</summary>
        public ICommand CloseCommand { get; }

        /// <summary>Shares the current image via the platform share sheet.</summary>
        public ICommand ShareCommand { get; }

        /// <summary>Saves the current image to the device Pictures library.</summary>
        public ICommand DownloadCommand { get; }

        /// <summary>Shows or hides the top/bottom chrome bars over the image.</summary>
        public ICommand ToggleChromeCommand { get; }

        public string ImageUri => Message.ImageUri;

        public bool HasImage => Message.HasImage;

        public string Caption => Message.Caption;

        public bool HasCaption => Message.HasCaption;

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

        /// <summary>Date + time of the message for the header subtitle.</summary>
        public string FormattedDateTime
        {
            get
            {
                DateTime ts = Message.Timestamp;
                if (ts == DateTime.MinValue)
                {
                    return string.Empty;
                }

                return ts.ToString("dd/MM/yyyy HH:mm");
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

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!Set(ref _isBusy, value))
                {
                    return;
                }

                RaiseExportCommandsChanged();
            }
        }

        private bool CanExport =>
            !string.IsNullOrWhiteSpace(ImageUri);

        private void RaiseExportCommandsChanged()
        {
            (DownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ShareCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private async System.Threading.Tasks.Task ShareAsync()
        {
            if (!CanExport || IsBusy)
            {
                return;
            }

            try
            {
                IsBusy = true;
                await _share.ShareLocalFileAsync(SenderDisplayName, ImageUri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ImageViewerViewModel] Share: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async System.Threading.Tasks.Task DownloadAsync()
        {
            if (!CanExport || IsBusy)
            {
                return;
            }

            try
            {
                IsBusy = true;
                string suggested = BuildSuggestedFileName();
                await _files.PickSaveLocalImageAsync(ImageUri, suggested);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ImageViewerViewModel] Download: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private string BuildSuggestedFileName()
        {
            string id = Message.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            }
            else
            {
                // Keep filesystem-safe short id.
                char[] chars = id.ToCharArray();
                for (int i = 0; i < chars.Length; i++)
                {
                    char c = chars[i];
                    if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                    {
                        chars[i] = '_';
                    }
                }

                id = new string(chars);
                if (id.Length > 40)
                {
                    id = id.Substring(0, 40);
                }
            }

            return "Unison_" + id + ".jpg";
        }
    }
}
