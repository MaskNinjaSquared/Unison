using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        private bool _attached;
        private bool _isEmpty = true;
        private StatusAuthorItem _selectedAuthor;

        public StatusListViewModel(IStatusService status, IDispatcher dispatcher)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            MenuCommand = new RelayCommand(() => MenuRequested?.Invoke(this, EventArgs.Empty));
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

        public ICommand MenuCommand { get; }

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
            _attached = true;
            _ = ReloadAsync();
        }

        public void Detach()
        {
            if (!_attached)
            {
                return;
            }

            _status.StatusUpdated -= Status_Updated;
            _attached = false;
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
            Authors.Clear();
            if (next != null)
            {
                for (int i = 0; i < next.Count; i++)
                {
                    if (next[i] != null)
                    {
                        Authors.Add(next[i]);
                    }
                }
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
    }
}
