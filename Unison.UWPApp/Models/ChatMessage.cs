using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Unison.UWPApp.Models
{
    public class ChatMessage : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _id;
        public string Id 
        { 
            get => _id; 
            set { _id = value; OnPropertyChanged(); } 
        }

        private string _content;
        public string Content 
        { 
            get => _content; 
            set { _content = value; OnPropertyChanged(); } 
        }

        private DateTime _timestamp;
        public DateTime Timestamp 
        { 
            get => _timestamp; 
            set { _timestamp = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedTime)); } 
        }

        private bool _isFromMe;
        public bool IsFromMe 
        { 
            get => _isFromMe; 
            set { _isFromMe = value; OnPropertyChanged(); } 
        }

        private string _status;
        public string Status 
        { 
            get => _status; 
            set { _status = value; OnPropertyChanged(); } 
        }

        private string _senderName;
        public string SenderName 
        { 
            get => _senderName; 
            set { _senderName = value; OnPropertyChanged(); } 
        }

        private bool _isImage;
        public bool IsImage
        {
            get => _isImage;
            set { _isImage = value; OnPropertyChanged(); }
        }

        private string _imageUri;
        public string ImageUri
        {
            get => _imageUri;
            set { _imageUri = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasImage)); }
        }

        private string _caption;
        public string Caption
        {
            get => _caption;
            set { _caption = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCaption)); }
        }

        private bool _isRunStart = true;
        public bool IsRunStart
        {
            get => _isRunStart;
            set
            {
                if (_isRunStart == value) return;
                _isRunStart = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowTail));
            }
        }

        private bool _isRunEnd = true;
        public bool IsRunEnd
        {
            get => _isRunEnd;
            set
            {
                if (_isRunEnd == value) return;
                _isRunEnd = value;
                OnPropertyChanged();
            }
        }

        public bool HasImage => !string.IsNullOrWhiteSpace(ImageUri);
        public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);
        public bool ShowTail => IsRunStart;

        public string FormattedTime => Timestamp.ToString("HH:mm");

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
