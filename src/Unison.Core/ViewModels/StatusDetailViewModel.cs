using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Status viewer: white progress segments, 5s photos, video duration from proto
    /// (or NaturalDuration), auto-advance, close after the last item.
    /// </summary>
    public sealed class StatusDetailViewModel : Observable
    {
        public const int PhotoDisplaySeconds = 5;

        private readonly IStatusService _status;
        private readonly IDispatcher _dispatcher;

        private IReadOnlyList<HistoryStatus> _items = Array.Empty<HistoryStatus>();
        private int _index;
        private string _authorJid;
        private string _authorName;
        private DateTime? _timestampUtc;
        private string _caption;
        private string _mediaUri;
        private bool _isVideo;
        private bool _isImage;
        private bool _isLoading;
        private bool _hasOpenAuthor;
        private bool _waitingForNaturalDuration;
        private CancellationTokenSource _timerCts;
        private int _openGeneration;

        public StatusDetailViewModel(IStatusService status, IDispatcher dispatcher)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            CloseCommand = new RelayCommand(Close);
            NextCommand = new RelayCommand(GoNext);
            PreviousCommand = new RelayCommand(GoPrevious);
        }

        public ObservableCollection<StatusProgressSegment> Segments { get; } =
            new ObservableCollection<StatusProgressSegment>();

        public string AuthorName
        {
            get => _authorName;
            private set => Set(ref _authorName, value);
        }

        public DateTime? TimestampUtc
        {
            get => _timestampUtc;
            private set => Set(ref _timestampUtc, value);
        }

        public string Caption
        {
            get => _caption;
            private set
            {
                if (Set(ref _caption, value))
                {
                    OnPropertyChanged(nameof(HasCaption));
                }
            }
        }

        public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);

        public string MediaUri
        {
            get => _mediaUri;
            private set => Set(ref _mediaUri, value);
        }

        public bool IsVideo
        {
            get => _isVideo;
            private set => Set(ref _isVideo, value);
        }

        public bool IsImage
        {
            get => _isImage;
            private set => Set(ref _isImage, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => Set(ref _isLoading, value);
        }

        public bool HasOpenAuthor
        {
            get => _hasOpenAuthor;
            private set => Set(ref _hasOpenAuthor, value);
        }

        public ICommand CloseCommand { get; }

        public ICommand NextCommand { get; }

        public ICommand PreviousCommand { get; }

        public event EventHandler Closed;

        public async Task OpenAuthorAsync(string authorJid, string displayName)
        {
            StopTimer();
            _openGeneration++;
            int generation = _openGeneration;
            _authorJid = authorJid;
            AuthorName = displayName ?? string.Empty;
            HasOpenAuthor = !string.IsNullOrWhiteSpace(authorJid);

            if (!HasOpenAuthor)
            {
                ResetVisual();
                return;
            }

            IReadOnlyList<HistoryStatus> items;
            try
            {
                items = await _status.GetActiveForAuthorAsync(authorJid).ConfigureAwait(false);
            }
            catch (Exception)
            {
                items = Array.Empty<HistoryStatus>();
            }

            if (generation != _openGeneration)
            {
                return;
            }

            _items = items ?? Array.Empty<HistoryStatus>();
            if (_items.Count == 0)
            {
                Close();
                return;
            }

            RebuildSegments(_items.Count);
            _index = 0;
            await ShowCurrentAsync(generation).ConfigureAwait(false);
        }

        public Task ClearAsync()
        {
            StopTimer();
            _openGeneration++;
            _authorJid = null;
            _items = Array.Empty<HistoryStatus>();
            HasOpenAuthor = false;
            ResetVisual();
            Segments.Clear();
            return Task.CompletedTask;
        }

        public void Close()
        {
            StopTimer();
            _openGeneration++;
            _authorJid = null;
            _items = Array.Empty<HistoryStatus>();
            HasOpenAuthor = false;
            ResetVisual();
            Segments.Clear();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public void GoNext()
        {
            if (!HasOpenAuthor)
            {
                return;
            }

            if (_index >= _items.Count - 1)
            {
                Close();
                return;
            }

            _index++;
            _ = ShowCurrentAsync(_openGeneration);
        }

        public void GoPrevious()
        {
            if (!HasOpenAuthor)
            {
                return;
            }

            if (_index <= 0)
            {
                _ = ShowCurrentAsync(_openGeneration);
                return;
            }

            _index--;
            _ = ShowCurrentAsync(_openGeneration);
        }

        /// <summary>
        /// When proto duration is 0, the view reports <see cref="MediaElement"/> natural duration.
        /// </summary>
        public void ApplyVideoNaturalDuration(double seconds)
        {
            if (!_waitingForNaturalDuration || !IsVideo)
            {
                return;
            }

            _waitingForNaturalDuration = false;
            double duration = seconds > 0.2 ? seconds : PhotoDisplaySeconds;
            StartTimer(TimeSpan.FromSeconds(duration));
        }

        /// <summary>Fallback if the video ends before the timer (shorter than proto seconds).</summary>
        public void NotifyVideoEnded()
        {
            if (!IsVideo || !HasOpenAuthor)
            {
                return;
            }

            GoNext();
        }

        public void Pause()
        {
            StopTimer();
        }

        private async Task ShowCurrentAsync(int generation)
        {
            StopTimer();
            _waitingForNaturalDuration = false;
            if (generation != _openGeneration || _index < 0 || _index >= _items.Count)
            {
                return;
            }

            HistoryStatus item = _items[_index];
            UpdateSegmentFills();
            TimestampUtc = item.TimestampUtc;
            Caption = item.Body;
            bool isVideo = item.Kind == ChatPreviewKind.Video;
            bool isImage = item.Kind == ChatPreviewKind.Image || item.Kind == ChatPreviewKind.Sticker;
            IsVideo = false;
            IsImage = false;
            MediaUri = null;
            IsLoading = isVideo || isImage;

            string uri = null;
            if (isVideo || isImage)
            {
                try
                {
                    uri = await _status.EnsureMediaAsync(item).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    uri = null;
                }
            }

            if (generation != _openGeneration)
            {
                return;
            }

            IsLoading = false;
            MediaUri = uri;
            IsVideo = isVideo && !string.IsNullOrWhiteSpace(uri);
            IsImage = isImage && !string.IsNullOrWhiteSpace(uri) && !IsVideo;

            if (IsVideo)
            {
                if (item.MediaDurationSeconds > 0)
                {
                    StartTimer(TimeSpan.FromSeconds(item.MediaDurationSeconds));
                }
                else
                {
                    _waitingForNaturalDuration = true;
                }
            }
            else
            {
                StartTimer(TimeSpan.FromSeconds(PhotoDisplaySeconds));
            }
        }

        private void RebuildSegments(int count)
        {
            Segments.Clear();
            for (int i = 0; i < count; i++)
            {
                Segments.Add(new StatusProgressSegment());
            }
        }

        private void UpdateSegmentFills()
        {
            for (int i = 0; i < Segments.Count; i++)
            {
                if (i < _index)
                {
                    Segments[i].Fill = 1;
                }
                else
                {
                    Segments[i].Fill = 0;
                }

                Segments[i].NotifyFillWidth();
            }
        }

        private void StartTimer(TimeSpan duration)
        {
            StopTimer();
            if (duration <= TimeSpan.Zero)
            {
                duration = TimeSpan.FromSeconds(PhotoDisplaySeconds);
            }

            var cts = new CancellationTokenSource();
            _timerCts = cts;
            int generation = _openGeneration;
            int index = _index;
            _ = RunTimerAsync(duration, cts.Token, generation, index);
        }

        private async Task RunTimerAsync(
            TimeSpan duration,
            CancellationToken token,
            int generation,
            int index)
        {
            DateTime start = DateTime.UtcNow;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    double elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
                    double fill = duration.TotalMilliseconds <= 0
                        ? 1
                        : Math.Min(1d, elapsed / duration.TotalMilliseconds);

                    await _dispatcher.RunAsync(() =>
                    {
                        if (generation != _openGeneration || index != _index || index >= Segments.Count)
                        {
                            return;
                        }

                        Segments[index].Fill = fill;
                        Segments[index].NotifyFillWidth();
                    }).ConfigureAwait(false);

                    if (fill >= 1d)
                    {
                        break;
                    }

                    await Task.Delay(50, token).ConfigureAwait(false);
                }

                if (!token.IsCancellationRequested && generation == _openGeneration)
                {
                    await _dispatcher.RunAsync(GoNext).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void StopTimer()
        {
            CancellationTokenSource cts = _timerCts;
            _timerCts = null;
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }

        private void ResetVisual()
        {
            AuthorName = string.Empty;
            TimestampUtc = null;
            Caption = null;
            MediaUri = null;
            IsVideo = false;
            IsImage = false;
            IsLoading = false;
            _waitingForNaturalDuration = false;
        }
    }
}
