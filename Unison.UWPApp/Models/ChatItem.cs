using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Unison.UWPApp.Models
{
    public class ChatItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _id;
        public string Id 
        { 
            get => _id; 
            set { _id = value; OnPropertyChanged(); } 
        }

        private string _jid;
        public string JID 
        { 
            get => _jid; 
            set { _jid = value; OnPropertyChanged(); } 
        }

        private string _name;
        public string Name 
        { 
            get => _name; 
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Initial)); } 
        }

        private string _lastMessage;
        public string LastMessage 
        { 
            get => _lastMessage; 
            set { _lastMessage = value; OnPropertyChanged(); } 
        }

        private string _timestamp;
        public string Timestamp 
        { 
            get => _timestamp; 
            set { _timestamp = value; OnPropertyChanged(); } 
        }

        private int _unreadCount;
        public int UnreadCount 
        { 
            get => _unreadCount; 
            set { _unreadCount = value; OnPropertyChanged(); } 
        }

        private string _avatarUrl;
        public string AvatarUrl 
        { 
            get => _avatarUrl; 
            set { _avatarUrl = value; OnPropertyChanged(); } 
        }

        private bool _isGroup;
        public bool IsGroup 
        { 
            get => _isGroup; 
            set { _isGroup = value; OnPropertyChanged(); } 
        }

        public string Initial => !string.IsNullOrEmpty(Name) ? Name.Substring(0, 1).ToUpper() : "?";

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
