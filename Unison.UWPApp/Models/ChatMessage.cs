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

        public string FormattedTime => Timestamp.ToString("HH:mm");

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
