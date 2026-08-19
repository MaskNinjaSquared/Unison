using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Views
{
    public sealed partial class StatusDetailView : UserControl
    {
        private bool _hooked;

        public StatusDetailViewModel ViewModel { get; private set; }

        public event EventHandler BackRequested;

        public bool HasOpenAuthor => ViewModel != null && ViewModel.HasOpenAuthor;

        public StatusDetailView()
        {
            if (App.Services != null)
            {
                ViewModel = App.Services.GetRequiredService<StatusDetailViewModel>();
                DataContext = ViewModel;
            }

            InitializeComponent();
            Loaded += StatusDetailView_Loaded;
            Unloaded += StatusDetailView_Unloaded;
        }

        public async System.Threading.Tasks.Task OpenAuthorAsync(StatusAuthorItem author)
        {
            if (ViewModel == null || author == null)
            {
                return;
            }

            await ViewModel.OpenAuthorAsync(author.Jid, author.DisplayName);
        }

        public async System.Threading.Tasks.Task ClearAsync()
        {
            if (ViewModel == null)
            {
                return;
            }

            StopVideo();
            await ViewModel.ClearAsync();
        }

        private void StatusDetailView_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || _hooked)
            {
                return;
            }

            _hooked = true;
            ViewModel.Closed += ViewModel_Closed;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.Segments.CollectionChanged += Segments_CollectionChanged;
        }

        private void StatusDetailView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || !_hooked)
            {
                return;
            }

            _hooked = false;
            ViewModel.Closed -= ViewModel_Closed;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.Segments.CollectionChanged -= Segments_CollectionChanged;
            ViewModel.Pause();
            StopVideo();
        }

        private void ViewModel_Closed(object sender, EventArgs e)
        {
            StopVideo();
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StatusDetailViewModel.MediaUri) ||
                e.PropertyName == nameof(StatusDetailViewModel.IsVideo) ||
                e.PropertyName == nameof(StatusDetailViewModel.IsImage))
            {
                ApplyMedia();
            }
        }

        private void Segments_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateSegmentWidths();
        }

        private void SegmentsHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSegmentWidths();
        }

        private void UpdateSegmentWidths()
        {
            if (ViewModel == null || SegmentsHost == null)
            {
                return;
            }

            int n = ViewModel.Segments.Count;
            if (n <= 0 || SegmentsHost.ActualWidth <= 0)
            {
                return;
            }

            const double gap = 3;
            double width = Math.Max(4, (SegmentsHost.ActualWidth - (gap * (n - 1))) / n);
            for (int i = 0; i < n; i++)
            {
                ViewModel.Segments[i].TrackWidth = width;
            }
        }

        private void ApplyMedia()
        {
            StopVideo();
            ViewerImage.Source = null;

            if (ViewModel == null)
            {
                return;
            }

            string uri = ViewModel.MediaUri;
            if (string.IsNullOrWhiteSpace(uri))
            {
                return;
            }

            Uri parsed;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed))
            {
                return;
            }

            if (ViewModel.IsVideo)
            {
                ViewerVideo.Source = parsed;
                ViewerVideo.Play();
            }
            else if (ViewModel.IsImage)
            {
                ViewerImage.Source = new BitmapImage(parsed);
            }
        }

        private void StopVideo()
        {
            try
            {
                ViewerVideo.Stop();
                ViewerVideo.Source = null;
            }
            catch
            {
            }
        }

        private void ViewerVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            if (ViewerVideo.NaturalDuration.HasTimeSpan)
            {
                ViewModel.ApplyVideoNaturalDuration(ViewerVideo.NaturalDuration.TimeSpan.TotalSeconds);
            }
        }

        private void ViewerVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            ViewModel?.NotifyVideoEnded();
        }

        private void LeftZone_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            ViewModel?.GoPrevious();
        }

        private void RightZone_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            ViewModel?.GoNext();
        }
    }
}
