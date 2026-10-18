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
        private readonly IRuntimeDiagnostics _diagnostics;

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
        private DateTime _itemShownUtc;
        private string _openingAuthorJid;

        public StatusDetailViewModel(
            IStatusService status,
            IDispatcher dispatcher,
            IRuntimeDiagnostics diagnostics = null)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _diagnostics = diagnostics;
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
            // SelectionChanged / list reload often re-raises the same author while EnsureMedia
            // is in flight. Bumping generation there cancelled show before any media log.
            if (IsSameAuthorOpen(authorJid))
            {
                LogStatus(
                    "open-skip-same",
                    "author=" + (authorJid ?? "-") +
                    "; opening=" + (_openingAuthorJid ?? "-") +
                    "; items=" + _items.Count);
                return;
            }

            StopTimer();
            _openGeneration++;
            int generation = _openGeneration;
            _authorJid = authorJid;
            _openingAuthorJid = authorJid;
            AuthorName = displayName ?? string.Empty;
            HasOpenAuthor = !string.IsNullOrWhiteSpace(authorJid);

            LogStatus(
                "open-start",
                "author=" + (authorJid ?? "-") + "; name=" + (displayName ?? "-") +
                "; gen=" + generation);

            if (!HasOpenAuthor)
            {
                _openingAuthorJid = null;
                ResetVisual();
                return;
            }

            IReadOnlyList<HistoryStatus> items;
            try
            {
                items = await _status.GetActiveForAuthorAsync(authorJid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogStatus("open-load-fail", "author=" + authorJid + "; err=" + ex.Message);
                items = Array.Empty<HistoryStatus>();
            }

            if (generation != _openGeneration)
            {
                LogStatus("open-stale", "author=" + authorJid + "; gen=" + generation);
                return;
            }

            // ObservableCollection + bound props must be touched on the UI dispatcher.
            await _dispatcher.RunAsync(() =>
            {
                if (generation != _openGeneration)
                {
                    return;
                }

                _items = items ?? Array.Empty<HistoryStatus>();
                LogStatus(
                    "open-loaded",
                    "author=" + authorJid + "; count=" + _items.Count + "; gen=" + generation);
                for (int i = 0; i < _items.Count; i++)
                {
                    LogStatus("open-item", StatusItemDiagnostics.DescribeItem(_items[i], i));
                }

                if (_items.Count == 0)
                {
                    return;
                }

                RebuildSegments(_items.Count);
                _index = 0;
            }).ConfigureAwait(false);

            if (generation != _openGeneration)
            {
                LogStatus("open-stale", "author=" + authorJid + "; gen=" + generation + "; after-ui");
                return;
            }

            if (_items.Count == 0)
            {
                _openingAuthorJid = null;
                Close("empty-items");
                return;
            }

            try
            {
                await ShowCurrentAsync(generation).ConfigureAwait(false);
            }
            finally
            {
                if (generation == _openGeneration &&
                    string.Equals(_openingAuthorJid, authorJid, StringComparison.OrdinalIgnoreCase))
                {
                    _openingAuthorJid = null;
                }
            }
        }

        private bool IsSameAuthorOpen(string authorJid)
        {
            if (string.IsNullOrWhiteSpace(authorJid))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_openingAuthorJid) &&
                string.Equals(_openingAuthorJid, authorJid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HasOpenAuthor &&
                   _items.Count > 0 &&
                   string.Equals(_authorJid, authorJid, StringComparison.OrdinalIgnoreCase);
        }

        public Task ClearAsync()
        {
            StopTimer();
            _openGeneration++;
            _authorJid = null;
            _openingAuthorJid = null;
            _items = Array.Empty<HistoryStatus>();
            HasOpenAuthor = false;
            ResetVisual();
            Segments.Clear();
            return Task.CompletedTask;
        }

        public void Close() => Close("user");

        private void Close(string reason)
        {
            LogStatus(
                "close",
                "reason=" + (reason ?? "-") +
                "; author=" + (_authorJid ?? "-") +
                "; index=" + _index +
                "; count=" + (_items?.Count ?? 0));
            StopTimer();
            _openGeneration++;
            _authorJid = null;
            _openingAuthorJid = null;
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
                Close("last-item");
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
            // W10M often reports 0 / tiny NaturalDuration for broken or still-opening streams.
            double duration = seconds >= 1d ? seconds : PhotoDisplaySeconds;
            LogStatus(
                "video-natural-dur",
                "raw=" + seconds.ToString("0.###") + "; used=" + duration.ToString("0.###"));
            StartTimer(TimeSpan.FromSeconds(duration));
        }

        /// <summary>Fallback if the video ends before the timer (shorter than proto seconds).</summary>
        public void NotifyVideoEnded()
        {
            if (!IsVideo || !HasOpenAuthor)
            {
                return;
            }

            double shownFor = (DateTime.UtcNow - _itemShownUtc).TotalSeconds;

            // Spurious MediaEnded on open (unsupported codec / zero-length) used to Close() instantly.
            if (_waitingForNaturalDuration)
            {
                _waitingForNaturalDuration = false;
                LogStatus("video-ended-early", "shownFor=" + shownFor.ToString("0.###") + "; fallback=photo");
                StartTimer(TimeSpan.FromSeconds(PhotoDisplaySeconds));
                return;
            }

            if (shownFor < 0.75)
            {
                LogStatus("video-ended-ignored", "shownFor=" + shownFor.ToString("0.###"));
                return;
            }

            LogStatus("video-ended", "shownFor=" + shownFor.ToString("0.###") + "; index=" + _index);
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
                LogStatus(
                    "show-aborted",
                    "gen=" + generation +
                    "; current=" + _openGeneration +
                    "; index=" + _index +
                    "; count=" + _items.Count);
                return;
            }

            HistoryStatus item = _items[_index];
            bool isVideo = item.Kind == ChatPreviewKind.Video;
            bool isImage = item.Kind == ChatPreviewKind.Image || item.Kind == ChatPreviewKind.Sticker;

            await _dispatcher.RunAsync(() =>
            {
                if (generation != _openGeneration)
                {
                    return;
                }

                UpdateSegmentFills();
                TimestampUtc = item.TimestampUtc;
                Caption = item.Body;
                IsVideo = false;
                IsImage = false;
                MediaUri = null;
                IsLoading = isVideo || isImage;
            }).ConfigureAwait(false);

            if (generation != _openGeneration)
            {
                LogStatus("show-aborted", "gen=" + generation + "; after-prep");
                return;
            }

            LogStatus(
                "show",
                StatusItemDiagnostics.DescribeItem(item, _index) +
                "; wantVideo=" + isVideo +
                "; wantImage=" + isImage);

            string uri = null;
            if (isVideo || isImage)
            {
                try
                {
                    uri = await _status.EnsureMediaAsync(item).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogStatus("ensure-media-fail", "id=" + item.MessageId + "; err=" + ex.Message);
                    uri = null;
                }
            }

            if (generation != _openGeneration)
            {
                LogStatus(
                    "show-aborted",
                    "gen=" + generation + "; after-media; id=" + item.MessageId);
                return;
            }

            string displayUri = StatusMediaDisplay.ResolveUri(uri, item);
            bool showVideo = StatusMediaDisplay.ShowAsVideo(item.Kind, displayUri);
            bool showImage = StatusMediaDisplay.ShowAsImage(item.Kind, displayUri);

            await _dispatcher.RunAsync(() =>
            {
                if (generation != _openGeneration)
                {
                    return;
                }

                IsLoading = false;
                MediaUri = displayUri;
                IsVideo = showVideo;
                IsImage = showImage;
                _itemShownUtc = DateTime.UtcNow;

                if (showVideo)
                {
                    // Proto seconds &lt; 1 are treated as unknown (often bad/zero-ish payloads).
                    // Always start a photo-length fallback so a dead MediaOpened cannot freeze at 0.
                    if (item.MediaDurationSeconds >= 1)
                    {
                        StartTimer(TimeSpan.FromSeconds(item.MediaDurationSeconds));
                    }
                    else
                    {
                        _waitingForNaturalDuration = true;
                        StartTimer(TimeSpan.FromSeconds(PhotoDisplaySeconds));
                        LogStatus("show-dur-fallback", "id=" + item.MessageId + "; protoDur=0");
                    }
                }
                else
                {
                    // Photo, sticker, text, thumb fallback, or failed media — always a readable beat.
                    StartTimer(TimeSpan.FromSeconds(PhotoDisplaySeconds));
                }
            }).ConfigureAwait(false);

            LogStatus(
                "show-resolved",
                "id=" + item.MessageId +
                "; displayUri=" + (displayUri ?? "-") +
                "; isVideo=" + showVideo +
                "; isImage=" + showImage +
                "; durSec=" + item.MediaDurationSeconds);

            if (!item.IsViewed)
            {
                item.IsViewed = true;
                _ = MarkViewedSafeAsync(item.AuthorJid, item.MessageId);
            }
        }

        private async Task MarkViewedSafeAsync(string authorJid, string messageId)
        {
            try
            {
                await _status.MarkViewedAsync(authorJid, messageId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogStatus("mark-viewed-fail", "id=" + messageId + "; err=" + ex.Message);
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
            if (duration.TotalSeconds < 1d)
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

        private void LogStatus(string eventName, string details)
        {
            _diagnostics?.Write("status", eventName, details);
        }
    }
}
