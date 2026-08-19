using System;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Views
{
    public sealed class StatusAuthorSelectedEventArgs : EventArgs
    {
        public StatusAuthorItem Author { get; }

        public StatusAuthorSelectedEventArgs(StatusAuthorItem author)
        {
            Author = author;
        }
    }

    public sealed partial class StatusListView : UserControl
    {
        private bool _hooked;
        private bool _suppressSelectionChanged;

        public StatusListViewModel ViewModel { get; private set; }

        public event EventHandler<StatusAuthorSelectedEventArgs> AuthorSelected;

        public event EventHandler MenuClicked;

        public event EventHandler SelectionCleared;

        public StatusListView()
        {
            if (App.Services != null)
            {
                ViewModel = App.Services.GetRequiredService<StatusListViewModel>();
                DataContext = ViewModel;
            }

            InitializeComponent();
            Loaded += StatusListView_Loaded;
            Unloaded += StatusListView_Unloaded;
        }

        private void StatusListView_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || _hooked)
            {
                return;
            }

            _hooked = true;
            ViewModel.SelectionCleared += ViewModel_SelectionCleared;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.Attach();
            SyncSelectionFromViewModel();
        }

        private void StatusListView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || !_hooked)
            {
                return;
            }

            _hooked = false;
            ViewModel.SelectionCleared -= ViewModel_SelectionCleared;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.Detach();
        }

        public void ClearSelection()
        {
            _suppressSelectionChanged = true;
            try
            {
                AuthorList.SelectedItem = null;
                if (ViewModel != null)
                {
                    ViewModel.SelectedAuthor = null;
                }
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
        }

        private void ViewModel_SelectionCleared(object sender, EventArgs e)
        {
            SelectionCleared?.Invoke(this, EventArgs.Empty);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StatusListViewModel.SelectedAuthor))
            {
                SyncSelectionFromViewModel();
            }
        }

        private void SyncSelectionFromViewModel()
        {
            if (ViewModel == null)
            {
                return;
            }

            _suppressSelectionChanged = true;
            try
            {
                AuthorList.SelectedItem = ViewModel.SelectedAuthor;
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
        }

        private void AuthorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged || ViewModel == null)
            {
                return;
            }

            var author = AuthorList.SelectedItem as StatusAuthorItem;
            if (author == null)
            {
                return;
            }

            ViewModel.SelectedAuthor = author;
            AuthorSelected?.Invoke(this, new StatusAuthorSelectedEventArgs(author));
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
