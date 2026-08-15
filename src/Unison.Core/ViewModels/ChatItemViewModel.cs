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

            Model.PropertyChanged += OnModelPropertyChanged;

        }



        private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)

        {

            PropertyChanged?.Invoke(this, e);



            if (IsDisplayNameSource(e?.PropertyName))

            {

                Raise(nameof(DisplayName), nameof(Initial));

            }



            if (e?.PropertyName == nameof(ChatItem.IsWidgetPinned))

            {

                Raise(nameof(IsWidgetPinned));

            }



            if (e?.PropertyName == nameof(ChatItem.IsChatPinned))

            {

                Raise(nameof(IsChatPinned));

            }


            if (e?.PropertyName == nameof(ChatItem.AvatarUrl) ||

                e?.PropertyName == nameof(ChatItem.AvatarHighUrl))

            {

                Raise(nameof(AvatarUrl));

            }



            if (e?.PropertyName == nameof(ChatItem.MutedUntil) ||

                e?.PropertyName == nameof(ChatItem.IsMutedLocally))

            {

                Raise(nameof(MutedUntil), nameof(IsMutedLocally));

            }

        }



        private static bool IsDisplayNameSource(string name) =>

            name == nameof(ChatItem.Name) ||

            name == nameof(ChatItem.Kind) ||

            name == nameof(ChatItem.IsPersonal);



        private void Raise(params string[] propertyNames)

        {

            var handler = PropertyChanged;

            if (handler == null || propertyNames == null)

            {

                return;

            }



            for (int i = 0; i < propertyNames.Length; i++)

            {

                handler(this, new PropertyChangedEventArgs(propertyNames[i]));

            }

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

        public string AvatarUrl => Model.GetAvatarUrl(preferHigh: false);

        public bool IsGroup => Model.IsGroup;

        public bool IsPersonal => Model.IsPersonal;

        public bool IsDirect => Model.IsDirect;

        public ChatKind Kind => Model.Kind;

        public bool IsChatPinned => Model.IsChatPinned;

        public bool IsWidgetPinned => Model.IsWidgetPinned;

        public long? MutedUntil => Model.MutedUntil;

        public bool IsMutedLocally => Model.IsMutedLocally;

        public ChatLocalStatus LocalStatus => Model.LocalStatus;

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

