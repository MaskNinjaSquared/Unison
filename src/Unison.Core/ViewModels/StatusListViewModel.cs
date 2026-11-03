using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>Status list: one row per author with unexpired items.</summary>
    public sealed class StatusListViewModel : Observable
    {
        private readonly IStatusService _status;
        private readonly IDispatcher _dispatcher;
        private readonly IStringResources _strings;
        private readonly IDialogService _dialogs;
        private readonly IProfileService _profile;
        private readonly IJidResolver _jids;
        private readonly List<StatusAuthorItem> _allAuthors = new List<StatusAuthorItem>();
        private bool _attached;
        private bool _isEmpty = true;
        private bool _isLoading = true;
        private StatusAuthorItem _selectedAuthor;
        private StatusAuthorItem _selfStatus;
        private StatusListFilter _activeFilter = StatusListFilter.All;
        private string _selfAvatarUrl;
        private bool _isMyStatusOpen;
        private readonly INavigator _navigator;
        private bool _isNewStatusPickerOpen;

        public StatusListViewModel(
            IStatusService status,
            IDispatcher dispatcher,
            IStringResources strings = null,
            IDialogService dialogs = null,
            IProfileService profile = null,
            IJidResolver jids = null,
            INavigator navigator = null)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _strings = strings;
            _dialogs = dialogs;
            _profile = profile;
            _jids = jids;
            _navigator = navigator;
            MenuCommand = new RelayCommand(() => MenuRequested?.Invoke(this, EventArgs.Empty));
            NewStatusCommand = new RelayCommand(OpenNewStatusPicker);
            AddStatusCommand = NewStatusCommand;
            CloseNewStatusPickerCommand = new RelayCommand(CloseNewStatusPicker);
            StartTextStatusCommand = new RelayCommand(StartTextStatus);
            StartMediaStatusCommand = new RelayCommand(() => { }, () => false);
            FilterStatusCommand = new RelayCommand<int>(ApplyStatusListFilter);
            RefreshSelfAvatar();
        }

        public ObservableCollection<StatusAuthorItem> Authors { get; } =
            new ObservableCollection<StatusAuthorItem>();

        public StatusAuthorItem SelectedAuthor
        {
            get => _selectedAuthor;
            set => Set(ref _selectedAuthor, value);
        }

        public bool IsEmpty
        {
            get => _isEmpty;
            private set => Set(ref _isEmpty, value);
        }

        /// <summary>True until the first author list arrives — Legacy (and others) show a spinner.</summary>
        public bool IsLoading
        {
            get => _isLoading;
            private set => Set(ref _isLoading, value);
        }

        public StatusListFilter ActiveFilter
        {
            get => _activeFilter;
            private set
            {
                if (Set(ref _activeFilter, value))
                {
                    OnPropertyChanged(nameof(IsFilterActive));
                }
            }
        }

        public bool IsFilterActive => _activeFilter != StatusListFilter.All;

        /// <summary>Logged-in user's avatar for the "My status" row.</summary>
        public string SelfAvatarUrl
        {
            get => _selfAvatarUrl;
            private set => Set(ref _selfAvatarUrl, value);
        }

        /// <summary>
        /// Own unexpired statuses as one row titled "My status", kept out of <see cref="Authors"/>;
        /// null when there are none.
        /// </summary>
        public StatusAuthorItem SelfStatus
        {
            get => _selfStatus;
            private set
            {
                if (Set(ref _selfStatus, value))
                {
                    OnPropertyChanged(nameof(HasSelfStatus));
                }
            }
        }

        public bool HasSelfStatus => _selfStatus != null;

        public ICommand MenuCommand { get; }

        /// <summary>Legacy app-bar new status: opens the Photo &amp; video / Text picker page.</summary>
        public ICommand NewStatusCommand { get; }

        /// <summary>Legacy "+" on the My status row — same picker page as <see cref="NewStatusCommand"/>.</summary>
        public ICommand AddStatusCommand { get; }

        public ICommand CloseNewStatusPickerCommand { get; }

        /// <summary>Opens the full-screen text status composer (Unison menu item / Legacy tile and app bar).</summary>
        public ICommand StartTextStatusCommand { get; }

        /// <summary>Photo &amp; video status — not available yet, so never executable.</summary>
        public ICommand StartMediaStatusCommand { get; }

        /// <summary>Legacy dock (attachment-bar style) with the two new-status tiles.</summary>
        public bool IsNewStatusPickerOpen
        {
            get => _isNewStatusPickerOpen;
            private set => Set(ref _isNewStatusPickerOpen, value);
        }

        public void OpenNewStatusPicker()
        {
            IsNewStatusPickerOpen = true;
        }

        public void CloseNewStatusPicker()
        {
            IsNewStatusPickerOpen = false;
        }

        /// <summary>Choosing a tile puts the Legacy dock away, as the attachment bar does.</summary>
        public void StartTextStatus()
        {
            CloseNewStatusPicker();
            _navigator?.NavigateFullScreen(NavigationDestination.StatusComposer);
        }

        /// <summary>Back lowers the Legacy new-status dock; false when it is not open.</summary>
        public bool TryHandleNewStatusBack()
        {
            if (_isNewStatusPickerOpen)
            {
                CloseNewStatusPicker();
                return true;
            }

            return false;
        }

        /// <summary>Flyout filter: All / Recent / Viewed.</summary>
        public ICommand FilterStatusCommand { get; }

        /// <summary>Own statuses (newest first) listed on the "My status" page with their view counts.</summary>
        public ObservableCollection<OwnStatusItem> OwnStatuses { get; } =
            new ObservableCollection<OwnStatusItem>();

        public bool IsMyStatusOpen
        {
            get => _isMyStatusOpen;
            private set => Set(ref _isMyStatusOpen, value);
        }

        public async Task OpenMyStatusAsync()
        {
            if (_selfStatus == null)
            {
                return;
            }

            IsMyStatusOpen = true;
            await ReloadOwnStatusesAsync().ConfigureAwait(false);
        }

        public void CloseMyStatus()
        {
            IsMyStatusOpen = false;
            OwnStatuses.Clear();
        }

        public event EventHandler MenuRequested;

        /// <summary>Selected author disappeared after a store reload.</summary>
        public event EventHandler SelectionCleared;

        public void Attach()
        {
            if (_attached)
            {
                return;
            }

            _status.StatusUpdated += Status_Updated;
            if (_profile != null)
            {
                _profile.ProfileChanged += Profile_Changed;
            }

            _attached = true;
            RefreshSelfAvatar();
            _ = ReloadAsync();
        }

        public void Detach()
        {
            if (!_attached)
            {
                return;
            }

            _status.StatusUpdated -= Status_Updated;
            if (_profile != null)
            {
                _profile.ProfileChanged -= Profile_Changed;
            }

            _attached = false;
        }

        private void Profile_Changed(object sender, EventArgs e)
        {
            _ = _dispatcher.RunAsync(RefreshSelfAvatar);
        }

        private void RefreshSelfAvatar()
        {
            Profile profile = _profile?.GetCurrentProfile();
            SelfAvatarUrl = profile?.AvatarUrl;
        }

        public async Task ReloadAsync()
        {
            IReadOnlyList<StatusAuthorItem> next;
            try
            {
                next = await _status.GetActiveAuthorsAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                next = Array.Empty<StatusAuthorItem>();
            }

            await _dispatcher.RunAsync(() => ApplyAuthors(next)).ConfigureAwait(false);
        }

        private void Status_Updated(object sender, EventArgs e)
        {
            _ = ReloadAsync();
            if (_isMyStatusOpen)
            {
                _ = ReloadOwnStatusesAsync();
            }
        }

        private async Task ReloadOwnStatusesAsync()
        {
            string selfJid = _selfStatus?.Jid;
            if (string.IsNullOrWhiteSpace(selfJid))
            {
                return;
            }

            IReadOnlyList<OwnStatusItem> next;
            try
            {
                next = await _status.GetOwnStatusesAsync(selfJid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusList] Own statuses load failed: " + ex.Message);
                next = Array.Empty<OwnStatusItem>();
            }

            await _dispatcher.RunAsync(() => ApplyOwnStatuses(next)).ConfigureAwait(false);
        }

        private void ApplyOwnStatuses(IReadOnlyList<OwnStatusItem> next)
        {
            OwnStatuses.Clear();
            if (!_isMyStatusOpen || next == null)
            {
                return;
            }

            for (int i = 0; i < next.Count; i++)
            {
                OwnStatusItem item = next[i];
                if (item == null)
                {
                    continue;
                }

                item.ViewCountText = ViewCountText(item.ViewCount);
                OwnStatuses.Add(item);
            }
        }

        private string ViewCountText(int count)
        {
            if (count == 1)
            {
                return _strings?.Get("StatusList_OneView", "1 view") ?? "1 view";
            }

            string format = _strings?.Get("StatusList_ViewCount", "{0} views") ?? "{0} views";
            try
            {
                return string.Format(format, count);
            }
            catch (FormatException)
            {
                return count.ToString();
            }
        }

        private void ApplyAuthors(IReadOnlyList<StatusAuthorItem> next)
        {
            string selectedJid = SelectedAuthor?.Jid;
            _allAuthors.Clear();
            if (next != null)
            {
                for (int i = 0; i < next.Count; i++)
                {
                    StatusAuthorItem author = next[i];
                    if (author == null)
                    {
                        continue;
                    }

                    author.ViewStateCaption = CaptionFor(author.ViewState);
                    _allAuthors.Add(author);
                }
            }

            IsLoading = false;
            RebuildVisible(selectedJid);
        }

        private void ApplyStatusListFilter(int filterId)
        {
            if (!Enum.IsDefined(typeof(StatusListFilter), filterId))
            {
                return;
            }

            var next = (StatusListFilter)filterId;
            if (next == ActiveFilter)
            {
                return;
            }

            ActiveFilter = next;
            string selectedJid = SelectedAuthor?.Jid;
            RebuildVisible(selectedJid);
        }

        private void RebuildVisible(string selectedJid)
        {
            Authors.Clear();
            StatusAuthorItem self = null;
            for (int i = 0; i < _allAuthors.Count; i++)
            {
                StatusAuthorItem author = _allAuthors[i];
                if (author == null)
                {
                    continue;
                }

                if (IsSelf(author.Jid))
                {
                    self = author;
                    continue;
                }

                if (!MatchesActiveFilter(author))
                {
                    continue;
                }

                Authors.Add(author);
            }

            SelfStatus = self != null ? ToSelfRow(self) : null;
            IsEmpty = Authors.Count == 0;

            StatusAuthorItem match = null;
            if (!string.IsNullOrWhiteSpace(selectedJid))
            {
                if (_selfStatus != null &&
                    string.Equals(_selfStatus.Jid, selectedJid, StringComparison.OrdinalIgnoreCase))
                {
                    match = _selfStatus;
                }

                for (int i = 0; match == null && i < Authors.Count; i++)
                {
                    if (string.Equals(Authors[i].Jid, selectedJid, StringComparison.OrdinalIgnoreCase))
                    {
                        match = Authors[i];
                    }
                }
            }

            SelectedAuthor = match;
            if (match == null && !string.IsNullOrWhiteSpace(selectedJid))
            {
                SelectionCleared?.Invoke(this, EventArgs.Empty);
            }
        }

        private bool IsSelf(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            if (_jids != null && _jids.IsSelfLinked(jid))
            {
                return true;
            }

            Profile self = _profile?.GetCurrentProfile();
            return SameJid(self?.Id, jid) || SameJid(self?.Lid, jid);
        }

        /// <summary>Why an author row was (or was not) treated as the account's own status.</summary>
        public string DescribeSelfMatch(StatusAuthorItem author)
        {
            string jid = author?.Jid;
            Profile profile = _profile?.GetCurrentProfile();
            Profile resolverSelf = _jids?.Self;
            string canonical = _jids != null && !string.IsNullOrWhiteSpace(jid) ? _jids.GetCanonicalJid(jid) : null;
            string alias = null;
            bool hasAlias = _jids != null && !string.IsNullOrWhiteSpace(jid) && _jids.TryGetAlias(jid, out alias);
            return "author=" + (jid ?? "<null>") +
                   " name=" + (author?.DisplayName ?? "<null>") +
                   " items=" + (author?.ItemCount ?? 0) +
                   " isSelf=" + IsSelf(jid) +
                   " selfLinked=" + (_jids != null && _jids.IsSelfLinked(jid)) +
                   " canonical=" + (canonical ?? "<null>") +
                   " alias=" + (hasAlias ? alias : "<none>") +
                   " resolverSelf.id=" + (resolverSelf?.Id ?? "<null>") +
                   " resolverSelf.lid=" + (resolverSelf?.Lid ?? "<null>") +
                   " profile.id=" + (profile?.Id ?? "<null>") +
                   " profile.lid=" + (profile?.Lid ?? "<null>") +
                   " hasJids=" + (_jids != null) +
                   " selfRow=" + (_selfStatus?.Jid ?? "<none>");
        }

        private static bool SameJid(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }

            return string.Equals(
                JidHelper.Normalize(a),
                JidHelper.Normalize(b),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Copy of the store row with "My status" as the title (and in the viewer header),
        /// the status thumb (account photo as fallback), and the update count as caption.
        /// </summary>
        private StatusAuthorItem ToSelfRow(StatusAuthorItem source)
        {
            string title = _strings?.Get("StatusList_MyStatus.Text", "My status") ?? "My status";
            string countFormat = _strings?.Get("StatusList_MyStatusCount", "{0} updates") ?? "{0} updates";
            string caption;
            try
            {
                caption = string.Format(countFormat, Math.Max(1, source.ItemCount));
            }
            catch (FormatException)
            {
                caption = Math.Max(1, source.ItemCount).ToString();
            }

            return new StatusAuthorItem
            {
                Jid = source.Jid,
                DisplayName = title,
                AvatarUrl = !string.IsNullOrWhiteSpace(source.AvatarUrl) ? source.AvatarUrl : SelfAvatarUrl,
                ViewState = source.ViewState,
                ViewStateCaption = caption,
                LatestTimestampUtc = source.LatestTimestampUtc,
                ItemCount = source.ItemCount
            };
        }

        private bool MatchesActiveFilter(StatusAuthorItem author)
        {
            switch (_activeFilter)
            {
                case StatusListFilter.All:
                    return true;
                case StatusListFilter.Recent:
                    return author.ViewState == StatusViewState.Recent;
                case StatusListFilter.Viewed:
                    return author.ViewState == StatusViewState.Viewed;
                default:
                    return true;
            }
        }

        private string CaptionFor(StatusViewState state)
        {
            if (state == StatusViewState.Viewed)
            {
                return _strings != null
                    ? _strings.Get("StatusList_Viewed", "Viewed")
                    : "Viewed";
            }

            return _strings != null
                ? _strings.Get("StatusList_Recent", "Recent")
                : "Recent";
        }
    }
}
