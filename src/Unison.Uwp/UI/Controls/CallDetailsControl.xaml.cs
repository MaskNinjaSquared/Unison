using System;
using System.Collections;
using System.Collections.Generic;
using Unison.Core.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class CallDetailsControl : UserControl
    {
        public event EventHandler BackRequested;

        public event EventHandler OpenChatRequested;

        public CallDetailsControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Binds the contact header, optional latest row above HISTORY, and flat older rows.
        /// </summary>
        public void BindSubject(
            CallDetailsSubject subject,
            CallHistoryEntry latest = null,
            IEnumerable historyRest = null,
            string historySectionTitle = null)
        {
            if (subject == null)
            {
                SubjectNameText.Text = string.Empty;
                SubjectAvatar.AvatarUrl = null;
                SubjectAvatar.IsGroup = false;
                LatestHost.Content = null;
                LatestHost.Visibility = Visibility.Collapsed;
                HistoryHeaderText.Visibility = Visibility.Collapsed;
                HistoryList.ItemsSource = null;
                EmptyHistoryText.Visibility = Visibility.Visible;
                HistoryScroll.Visibility = Visibility.Collapsed;
                OpenChatButton.IsEnabled = false;
                return;
            }

            SubjectNameText.Text = subject.DisplayName ?? string.Empty;
            SubjectAvatar.AvatarUrl = subject.AvatarUrl;
            SubjectAvatar.IsGroup = subject.IsGroup;
            OpenChatButton.IsEnabled = !string.IsNullOrWhiteSpace(subject.Jid);

            bool hasLatest = latest != null;
            bool hasRest = !IsEmpty(historyRest);
            bool empty = !hasLatest && !hasRest;
            EmptyHistoryText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            HistoryScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            if (empty)
            {
                LatestHost.Content = null;
                LatestHost.Visibility = Visibility.Collapsed;
                HistoryHeaderText.Visibility = Visibility.Collapsed;
                HistoryList.ItemsSource = null;
                return;
            }

            LatestHost.Content = latest;
            LatestHost.Visibility = hasLatest ? Visibility.Visible : Visibility.Collapsed;

            HistoryHeaderText.Text = string.IsNullOrWhiteSpace(historySectionTitle)
                ? "HISTORY"
                : historySectionTitle;
            HistoryHeaderText.Visibility = hasRest ? Visibility.Visible : Visibility.Collapsed;
            HistoryList.ItemsSource = hasRest ? historyRest : null;
        }

        /// <summary>Legacy overload used while shells migrate — treats first history row as latest.</summary>
        public void BindSubject(
            CallDetailsSubject subject,
            IEnumerable history,
            IEnumerable groupedHistory)
        {
            CallHistoryEntry latest = null;
            var rest = new List<CallHistoryEntry>();
            if (history != null)
            {
                foreach (object row in history)
                {
                    var entry = row as CallHistoryEntry;
                    if (entry == null)
                    {
                        continue;
                    }

                    if (latest == null)
                    {
                        latest = entry;
                    }
                    else
                    {
                        rest.Add(entry);
                    }
                }
            }

            BindSubject(subject, latest, rest);
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
    }
}
