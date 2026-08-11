using System;
using System.ComponentModel;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public class ChatItemViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private readonly IStringResources _strings;

        public ChatItem Model { get; }

        public ChatItemViewModel(ChatItem model, IStringResources strings = null)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            _strings = strings;
            Model.PropertyChanged += (s, e) =>
            {
                PropertyChanged?.Invoke(this, e);
                if (e.PropertyName == nameof(ChatItem.Name) ||
                    e.PropertyName == nameof(ChatItem.Kind) ||
                    e.PropertyName == nameof(ChatItem.IsPersonal))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Initial)));
                }
            };
        }

        public string JID => Model.JID;

        /// <summary>Raw stored name (no localized self marker).</summary>
        public string Name => Model.Name;

        /// <summary>List/header label via <see cref="ChatItem.GetNameResolved"/>.</summary>
        public string DisplayName => Model.GetNameResolved(_strings);

        public string LastMessage => Model.LastMessage;
        public ChatPreviewKind LastMessageKind => Model.LastMessageKind;
        public bool IsLastMessageImage => Model.IsLastMessageImage;
        public bool IsLastMessageVideo => Model.IsLastMessageVideo;
        public bool IsLastMessageSticker => Model.IsLastMessageSticker;
        public bool IsLastMessageVoice => Model.IsLastMessageVoice;
        public bool IsLastMessageText => Model.IsLastMessageText;
        public string Timestamp => Model.Timestamp;
        public string AvatarUrl => Model.AvatarUrl;
        public bool IsGroup => Model.IsGroup;
        public bool IsPersonal => Model.IsPersonal;
        public bool IsDirect => Model.IsDirect;
        public ChatKind Kind => Model.Kind;
        public bool IsPinned => Model.IsPinned;
        public int UnreadCount => Model.UnreadCount;
        public string UnreadText => Model.UnreadText;
        public bool HasUnread => Model.HasUnread;

        public string Initial
        {
            get
            {
                string label = DisplayName;
                return !string.IsNullOrEmpty(label) ? label.Substring(0, 1).ToUpper() : "?";
            }
        }
    }
}
