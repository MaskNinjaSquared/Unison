using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// How many chats currently have a notification (an unread count), read straight off the
    /// in-memory list. The shell brand and the chat-list header both show this, so the count
    /// lives here rather than in either view model.
    /// </summary>
    public static class NotificationCounter
    {
        public static int CountChatsWithNotification(System.Collections.Generic.IEnumerable<ChatItem> chats)
        {
            if (chats == null)
            {
                return 0;
            }

            int count = 0;
            foreach (ChatItem chat in chats)
            {
                if (chat != null && chat.HasUnread)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Superscript plus (U+207A / AltGr+), Unigram Mobile style for overflow past 99.
        /// </summary>
        public const char SuperscriptPlus = '\u207A';

        /// <summary>Parenthetical for the header, or empty when nothing is unread.</summary>
        public static string FormatBadge(int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            // Mobile / Unigram: "99⁺" with a raised plus — not a leading "+99".
            return count > 99
                ? "(99" + SuperscriptPlus + ")"
                : "(" + count + ")";
        }

        /// <summary>
        /// Compact overflow for Live Tile / toast captions: "99⁺" when count &gt; 99.
        /// </summary>
        public static string FormatOverflowCount(int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            return count > 99
                ? "99" + SuperscriptPlus
                : count.ToString();
        }
    }

    /// <summary>
    /// Keeps <see cref="Count"/> current as chats arrive, leave, or change their unread count.
    /// </summary>
    public sealed class NotificationCounterWatch : IDisposable
    {
        private readonly ObservableCollection<ChatItem> _chats;
        private bool _disposed;

        public NotificationCounterWatch(ObservableCollection<ChatItem> chats)
        {
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _chats.CollectionChanged += Chats_CollectionChanged;
            for (int i = 0; i < _chats.Count; i++)
            {
                Hook(_chats[i]);
            }

            Count = NotificationCounter.CountChatsWithNotification(_chats);
        }

        public event EventHandler Changed;

        public int Count { get; private set; }

        public string BadgeText => NotificationCounter.FormatBadge(Count);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _chats.CollectionChanged -= Chats_CollectionChanged;
            for (int i = 0; i < _chats.Count; i++)
            {
                Unhook(_chats[i]);
            }
        }

        private void Chats_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                for (int i = 0; i < e.OldItems.Count; i++)
                {
                    Unhook(e.OldItems[i] as ChatItem);
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                for (int i = 0; i < _chats.Count; i++)
                {
                    Hook(_chats[i]);
                }
            }
            else if (e.NewItems != null)
            {
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    Hook(e.NewItems[i] as ChatItem);
                }
            }

            Publish();
        }

        private void Chat_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatItem.UnreadCount))
            {
                Publish();
            }
        }

        private void Publish()
        {
            int next = NotificationCounter.CountChatsWithNotification(_chats);
            if (next == Count)
            {
                return;
            }

            Count = next;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Hook(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            chat.PropertyChanged -= Chat_PropertyChanged;
            chat.PropertyChanged += Chat_PropertyChanged;
        }

        private void Unhook(ChatItem chat)
        {
            if (chat != null)
            {
                chat.PropertyChanged -= Chat_PropertyChanged;
            }
        }
    }
}
