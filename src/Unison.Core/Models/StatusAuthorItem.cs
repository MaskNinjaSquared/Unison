using System;
using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    /// <summary>
    /// One Status list row: a person with at least one unexpired item.
    /// </summary>
    public sealed class StatusAuthorItem : Observable
    {
        private string _jid;
        private string _displayName;
        private string _avatarUrl;
        private DateTime? _latestTimestampUtc;
        private int _itemCount;

        public string Jid
        {
            get => _jid;
            set => Set(ref _jid, value);
        }

        public string DisplayName
        {
            get => _displayName;
            set => Set(ref _displayName, value);
        }

        public string AvatarUrl
        {
            get => _avatarUrl;
            set => Set(ref _avatarUrl, value);
        }

        public DateTime? LatestTimestampUtc
        {
            get => _latestTimestampUtc;
            set => Set(ref _latestTimestampUtc, value);
        }

        public int ItemCount
        {
            get => _itemCount;
            set => Set(ref _itemCount, value);
        }
    }
}
