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
        private string _viewStateCaption;
        private DateTime? _latestTimestampUtc;
        private int _itemCount;
        private StatusViewState _viewState;

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

        /// <summary><see cref="StatusViewState.Recent"/> or <see cref="StatusViewState.Viewed"/>.</summary>
        public StatusViewState ViewState
        {
            get => _viewState;
            set
            {
                if (Set(ref _viewState, value))
                {
                    OnPropertyChanged(nameof(IsRecent));
                }
            }
        }

        /// <summary>Localized "Recent" / "Viewed" line under the name.</summary>
        public string ViewStateCaption
        {
            get => _viewStateCaption;
            set => Set(ref _viewStateCaption, value);
        }

        public bool IsRecent => ViewState == StatusViewState.Recent;

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
