using System;
using System.Collections;
using System.Collections.Generic;
using Unison.Core.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class CallDetailsControl : UserControl
    {
        private readonly CollectionViewSource _historyCvs = new CollectionViewSource
        {
            IsSourceGrouped = true
        };

        private CallHistoryDateGroup _pendingJumpGroup;

        public event EventHandler BackRequested;

        public event EventHandler OpenChatRequested;

        public CallDetailsControl()
        {
            InitializeComponent();
            HistoryZoom.ViewChangeStarted += HistoryZoom_ViewChangeStarted;
            HistoryZoom.ViewChangeCompleted += HistoryZoom_ViewChangeCompleted;
        }

        public void BindSubject(
            CallDetailsSubject subject,
            IEnumerable history = null,
            IEnumerable groupedHistory = null)
        {
            if (subject == null)
            {
                SubjectNameText.Text = string.Empty;
                SubjectAvatar.AvatarUrl = null;
                SubjectAvatar.IsGroup = false;
                _historyCvs.Source = null;
                HistoryList.ItemsSource = null;
                HistoryJumpList.ItemsSource = null;
                EmptyHistoryText.Visibility = Visibility.Visible;
                HistoryZoom.Visibility = Visibility.Collapsed;
                OpenChatButton.IsEnabled = false;
                return;
            }

            SubjectNameText.Text = subject.DisplayName ?? string.Empty;
            SubjectAvatar.AvatarUrl = subject.AvatarUrl;
            SubjectAvatar.IsGroup = subject.IsGroup;
            OpenChatButton.IsEnabled = !string.IsNullOrWhiteSpace(subject.Jid);

            bool empty = IsEmpty(groupedHistory) && IsEmpty(history);
            EmptyHistoryText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            HistoryZoom.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            if (empty)
            {
                _historyCvs.Source = null;
                HistoryList.ItemsSource = null;
                HistoryJumpList.ItemsSource = null;
                return;
            }

            if (groupedHistory != null)
            {
                // Rebind Source + ItemsSource together (Favorites SemanticZoom pattern).
                _historyCvs.Source = groupedHistory;
                HistoryList.ItemsSource = _historyCvs.View;
                HistoryJumpList.ItemsSource = groupedHistory;
            }
            else
            {
                _historyCvs.Source = null;
                HistoryList.ItemsSource = history;
                HistoryJumpList.ItemsSource = null;
            }
        }

        private static bool IsEmpty(IEnumerable source)
        {
            if (source == null)
            {
                return true;
            }

            var collection = source as ICollection;
            if (collection != null)
            {
                return collection.Count == 0;
            }

            IEnumerator e = source.GetEnumerator();
            try
            {
                return !e.MoveNext();
            }
            finally
            {
                var disposable = e as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OpenChatButton_Click(object sender, RoutedEventArgs e)
        {
            OpenChatRequested?.Invoke(this, EventArgs.Empty);
        }

        private void HistoryJumpList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var group = e.ClickedItem as CallHistoryDateGroup;
            if (group == null || group.Count == 0)
            {
                return;
            }

            // ScrollIntoView(group) fails inside SemanticZoom — MakeVisible first item
            // after zoom-in (same fix as Favorites / ContactsVisualizer).
            _pendingJumpGroup = group;
            if (!HistoryZoom.IsZoomedInViewActive)
            {
                HistoryZoom.IsZoomedInViewActive = true;
                return;
            }

            ScheduleScrollToPendingGroup();
        }

        private void HistoryZoom_ViewChangeStarted(object sender, SemanticZoomViewChangedEventArgs e)
        {
            if (e == null || e.IsSourceZoomedInView)
            {
                return;
            }

            CallHistoryDateGroup pending = _pendingJumpGroup;
            if (pending != null && pending.Count > 0)
            {
                e.DestinationItem.Item = pending[0];
            }
        }

        private void HistoryZoom_ViewChangeCompleted(object sender, SemanticZoomViewChangedEventArgs e)
        {
            if (e == null || e.IsSourceZoomedInView)
            {
                return;
            }

            ScheduleScrollToPendingGroup();
        }

        private void ScheduleScrollToPendingGroup()
        {
            CallHistoryDateGroup pending = _pendingJumpGroup;
            _pendingJumpGroup = null;
            if (pending == null || pending.Count == 0 || HistoryList == null)
            {
                return;
            }

            CallHistoryEntry first = pending[0];
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    HistoryList.MakeVisible(new SemanticZoomLocation { Item = first });
                }
                catch
                {
                    // Best-effort jump; list may not be realized yet.
                }
            });
        }
    }
}
