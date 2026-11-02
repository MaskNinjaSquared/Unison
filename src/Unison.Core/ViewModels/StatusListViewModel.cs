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
        private readonly List<StatusAuthorItem> _allAuthors = new List<StatusAuthorItem>();
        private bool _attached;
        private bool _isEmpty = true;
        private bool _isLoading = true;
        private StatusAuthorItem _selectedAuthor;
        private StatusListFilter _activeFilter = StatusListFilter.All;
        private string _selfAvatarUrl;

        public StatusListViewModel(
            IStatusService status,
            IDispatcher dispatcher,
            IStringResources strings = null,
            IDialogService dialogs = null,
            IProfileService profile = null)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _strings = strings;
            _dialogs = dialogs;
            _profile = profile;
            MenuCommand = new RelayCommand(() => MenuRequested?.Invoke(this, EventArgs.Empty));
            NewStatusCommand = new RelayCommand(() => _ = ShowNewStatusUnavailableAsync());
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

        public ICommand MenuCommand { get; }

        /// <summary>My status / app-bar new status — compose not wired; shows unavailable dialog.</summary>
        public ICommand NewStatusCommand { get; }

        /// <summary>Flyout filter: All / Recent / Viewed.</summary>
        public ICommand FilterStatusCommand { get; }

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

        private async Task ShowNewStatusUnavailableAsync()
        {
            if (_dialogs == null)
            {
                return;
            }

            string title = _strings?.Get("StatusList_MyStatus.Text", "My status") ?? "My status";
            string body = _strings?.Get(
                "StatusList_NewStatusUnavailable",
                "This option is not available yet.")
                ?? "This option is not available yet.";
            string close = _strings?.Get("Common_Close", "Close") ?? "Close";
            try
            {
                await _dialogs.ShowMessageAsync(title, body, close).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusList] New status unavailable dialog: " + ex.Message);
            }
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
            for (int i = 0; i < _allAuthors.Count; i++)
            {
                StatusAuthorItem author = _allAuthors[i];
                if (author == null || !MatchesActiveFilter(author))
                {
                    continue;
                }

                Authors.Add(author);
            }

            IsEmpty = Authors.Count == 0;

            StatusAuthorItem match = null;
            if (!string.IsNullOrWhiteSpace(selectedJid))
            {
                for (int i = 0; i < Authors.Count; i++)
                {
                    if (string.Equals(Authors[i].Jid, selectedJid, StringComparison.OrdinalIgnoreCase))
                    {
                        match = Authors[i];
                        break;
                    }
                }
            }

            SelectedAuthor = match;
            if (match == null && !string.IsNullOrWhiteSpace(selectedJid))
            {
                SelectionCleared?.Invoke(this, EventArgs.Empty);
            }
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
