using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Views
{
    public sealed partial class StatusDetailView : UserControl
    {
        private bool _hooked;
        private bool _pendingVideoPlay;
        private Image _viewerImage;
        private MediaElement _viewerVideo;

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
            if (ViewerRoot != null)
            {
                ViewerRoot.SizeChanged += ViewerRoot_SizeChanged;
            }

            if (KindHost != null)
            {
                KindHost.DataContextChanged += KindHost_DataContextChanged;
            }
        }

        public async System.Threading.Tasks.Task OpenAuthorAsync(StatusAuthorItem author, string startMessageId = null)
        {
            if (ViewModel == null || author == null)
            {
                return;
            }

            await ViewModel.OpenAuthorAsync(author.Jid, author.DisplayName, startMessageId);
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
            ResolveMediaHosts();
            ApplyMedia();
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
            DetachVideoHandlers();
            StopVideo();
        }

        private void KindHost_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            ResolveMediaHosts();
            ApplyMedia();
        }

        private void ViewModel_Closed(object sender, EventArgs e)
        {
            StopVideo();
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StatusDetailViewModel.CurrentKind))
            {
                // Template swap is async to the visual tree; resolve hosts on next tick.
                Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    ResolveMediaHosts();
                    ApplyMedia();
                });
                return;
            }

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

        private void ResolveMediaHosts()
        {
            DetachVideoHandlers();
            _viewerImage = FindDescendant<Image>(KindHost);
            _viewerVideo = FindDescendant<MediaElement>(KindHost);
            if (_viewerVideo != null)
            {
                _viewerVideo.MediaOpened += ViewerVideo_MediaOpened;
                _viewerVideo.MediaEnded += ViewerVideo_MediaEnded;
            }
        }

        private void DetachVideoHandlers()
        {
            if (_viewerVideo == null)
            {
                return;
            }

            _viewerVideo.MediaOpened -= ViewerVideo_MediaOpened;
            _viewerVideo.MediaEnded -= ViewerVideo_MediaEnded;
        }

        private void ApplyMedia()
        {
            _pendingVideoPlay = false;
            StopVideo();
            if (_viewerImage != null)
            {
                _viewerImage.Source = null;
            }

            if (ViewModel == null)
            {
                return;
            }

            if (_viewerImage == null && _viewerVideo == null)
            {
                ResolveMediaHosts();
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

            if (ViewModel.IsVideo && _viewerVideo != null)
            {
                _viewerVideo.Source = parsed;
                if (HasMeasuredVideoViewport())
                {
                    RemeasureVideo();
                    _viewerVideo.Play();
                }
                else
                {
                    _pendingVideoPlay = true;
                }
            }
            else if (ViewModel.IsImage && _viewerImage != null)
            {
                _viewerImage.Source = new BitmapImage(parsed);
            }
        }

        private void StopVideo()
        {
            _pendingVideoPlay = false;
            if (_viewerVideo == null)
            {
                return;
            }

            try
            {
                _viewerVideo.Stop();
                _viewerVideo.Source = null;
            }
            catch
            {
            }
        }

        private void ViewerRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!HasMeasuredVideoViewport())
            {
                return;
            }

            if (_pendingVideoPlay && ViewModel != null && ViewModel.IsVideo && _viewerVideo != null && _viewerVideo.Source != null)
            {
                _pendingVideoPlay = false;
                RemeasureVideo();
                try
                {
                    _viewerVideo.Play();
                }
                catch
                {
                }

                return;
            }

            if (ViewModel != null && ViewModel.IsVideo && _viewerVideo != null && _viewerVideo.Source != null)
            {
                RemeasureVideo();
            }
        }

        private bool HasMeasuredVideoViewport()
        {
            return ViewerRoot != null &&
                   ViewerRoot.ActualWidth > 1 &&
                   ViewerRoot.ActualHeight > 1;
        }

        private void RemeasureVideo()
        {
            if (_viewerVideo == null)
            {
                return;
            }

            try
            {
                _viewerVideo.InvalidateMeasure();
                _viewerVideo.InvalidateArrange();
                ViewerRoot?.UpdateLayout();
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

            if (HasMeasuredVideoViewport())
            {
                RemeasureVideo();
            }
            else
            {
                _pendingVideoPlay = true;
            }

            double seconds = _viewerVideo != null && _viewerVideo.NaturalDuration.HasTimeSpan
                ? _viewerVideo.NaturalDuration.TimeSpan.TotalSeconds
                : 0d;
            ViewModel.ApplyVideoNaturalDuration(seconds);
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

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                T match = child as T;
                if (match != null)
                {
                    return match;
                }

                match = FindDescendant<T>(child);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
