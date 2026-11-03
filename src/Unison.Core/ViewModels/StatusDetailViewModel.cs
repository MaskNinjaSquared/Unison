using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
        private readonly IFilePicker _files;
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
        private bool _canDownload;
        private bool _isSaving;
        private bool _waitingForNaturalDuration;
        private CancellationTokenSource _timerCts;
        private int _openGeneration;
        private DateTime _itemShownUtc;
        private TimeSpan _itemDuration;
        private string _openingAuthorJid;
        private string _startMessageId;
        private readonly RelayCommand _downloadCommand;

        public StatusDetailViewModel(
            IStatusService status,
            IDispatcher dispatcher,
            IFilePicker files = null,
            IRuntimeDiagnostics diagnostics = null)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _files = files;
            _diagnostics = diagnostics;
            CloseCommand = new RelayCommand(Close);
            NextCommand = new RelayCommand(GoNext);
            PreviousCommand = new RelayCommand(GoPrevious);
            _downloadCommand = new RelayCommand(
                () => _ = DownloadCurrentAsync(),
                () => CanDownload && !_isSaving);
            DownloadCommand = _downloadCommand;
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

        /// <summary>
        /// True when the open Status item has local image/video ready to save (Unison-only).
        /// </summary>
        public bool CanDownload
        {
            get => _canDownload;
            private set
            {
                if (Set(ref _canDownload, value))
                {
                    _downloadCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public ICommand CloseCommand { get; }

        public ICommand NextCommand { get; }

        public ICommand PreviousCommand { get; }

        public ICommand DownloadCommand { get; }

        public event EventHandler Closed;

        /// <param name="startMessageId">Status to show first (own "My status" page); null starts at the oldest.</param>
        public async Task OpenAuthorAsync(string authorJid, string displayName, string startMessageId = null)
        {
            // SelectionChanged / list reload often re-raises the same author while EnsureMedia
            // is in flight. Bumping generation there cancelled show before any media log.
            if (IsSameAuthorOpen(authorJid))
            {
                if (TryJumpTo(startMessageId))
                {
                    return;
                }

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
            _startMessageId = startMessageId;
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
                    LogStatus("open-item", _status.DescribeStatusItem(_items[i], i));
                }

                if (_items.Count == 0)
                {
                    return;
                }

                RebuildSegments(_items.Count);
                _index = StatusStartIndex.Resolve(_items, _startMessageId);
                _startMessageId = null;
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

        /// <summary>
        /// Same author re-opened on another item: still loading → remember the target;
        /// already showing → move there. False when there is nothing to jump to.
        /// </summary>
        private bool TryJumpTo(string startMessageId)
        {
            if (string.IsNullOrWhiteSpace(startMessageId))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_openingAuthorJid))
            {
                _startMessageId = startMessageId;
                return true;
            }

            int? target = StatusStartIndex.Find(_items, startMessageId);
            if (!target.HasValue || target.Value == _index)
            {
                return false;
            }

            LogStatus("open-jump", "from=" + _index + "; to=" + target.Value + "; id=" + startMessageId);
            _index = target.Value;
            _ = ShowCurrentAsync(_openGeneration);
            return true;
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
            _startMessageId = null;
            _items = Array.Empty<HistoryStatus>();
            HasOpenAuthor = false;
            ResetVisual();
            Segments.Clear();
            return Task.CompletedTask;
        }

        public void Close() => Close("user");

        /// <summary>
        /// Unison-only: FileSavePicker copies the current Status media out of the app cache.
        /// Pauses the auto-advance timer while the picker is open.
        /// </summary>
        public async Task DownloadCurrentAsync()
        {
            if (_files == null || _isSaving || !CanDownload)
            {
                return;
            }

            HistoryStatus item = CurrentItem();
            string uri = MediaUri;
            double fill = (_index >= 0 && _index < Segments.Count) ? Segments[_index].Fill : 0d;
            TimeSpan remaining = TimeSpan.FromMilliseconds(
                _itemDuration.TotalMilliseconds * Math.Max(0.05d, 1d - fill));

            _isSaving = true;
            _downloadCommand.RaiseCanExecuteChanged();
            StopTimer();
            LogStatus("download-start", "uri=" + (uri ?? "-") + "; id=" + (item?.MessageId ?? "-"));

            try
            {
                if (item != null)
                {
                    try
                    {
                        string ensured = await _status.EnsureMediaAsync(item).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(ensured))
                        {
                            uri = ensured;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogStatus("download-ensure-fail", "err=" + ex.Message);
                    }
                }

                if (string.IsNullOrWhiteSpace(uri))
                {
                    LogStatus("download-skip", "no-uri");
                    return;
                }

                string suggested = BuildSuggestedFileName(item, uri);
                string saved;
                if (IsVideo)
                {
                    saved = await _files
                        .PickSaveLocalFileAsync(uri, suggested, item?.MediaMimeType)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Prefer generic save so webp/jpeg/png keep a matching extension.
                    saved = await _files
                        .PickSaveLocalFileAsync(uri, suggested, item?.MediaMimeType)
                        .ConfigureAwait(false);
                }

                LogStatus(
                    saved == null ? "download-cancel" : "download-ok",
                    "path=" + (saved ?? "-"));
            }
            catch (Exception ex)
            {
                LogStatus("download-fail", "err=" + ex.Message);
            }
            finally
            {
                _isSaving = false;
                await _dispatcher.RunAsync(() =>
                {
                    _downloadCommand.RaiseCanExecuteChanged();
                    RefreshCanDownload();
                    if (HasOpenAuthor && _items.Count > 0)
                    {
                        StartTimer(remaining);
                    }
                }).ConfigureAwait(false);
            }
        }

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
            _startMessageId = null;
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
                RefreshCanDownload();
            }).ConfigureAwait(false);

            if (generation != _openGeneration)
            {
                LogStatus("show-aborted", "gen=" + generation + "; after-prep");
                return;
            }

            LogStatus(
                "show",
                _status.DescribeStatusItem(item, _index) +
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

            string displayUri = _status.ResolveMediaDisplayUri(uri, item);
            bool showVideo = _status.ShowStatusAsVideo(item.Kind, displayUri);
            bool showImage = _status.ShowStatusAsImage(item.Kind, displayUri);

            await _dispatcher.RunAsync(() =>
            {
                if (generation != _openGeneration)
                {
                    return;
                }

                IsLoading = false;
                // Visibility (IsVideo/IsImage) before MediaUri so MediaElement is Visible
                // when ApplyMedia assigns Source — MediaUri-first left it Collapsed on reopen.
                IsVideo = showVideo;
                IsImage = showImage;
                MediaUri = displayUri;
                RefreshCanDownload();
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

            _itemDuration = duration;
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
            RefreshCanDownload();
        }

        private HistoryStatus CurrentItem()
        {
            if (_items == null || _index < 0 || _index >= _items.Count)
            {
                return null;
            }

            return _items[_index];
        }

        private void RefreshCanDownload()
        {
            CanDownload = !IsLoading &&
                          !_isSaving &&
                          (IsImage || IsVideo) &&
                          !string.IsNullOrWhiteSpace(MediaUri);
        }

        private static string BuildSuggestedFileName(HistoryStatus item, string uri)
        {
            string extension = ExtensionFrom(item, uri);
            string id = item?.MessageId;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            }
            else if (id.Length > 12)
            {
                id = id.Substring(0, 12);
            }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                id = id.Replace(c, '_');
            }

            return "Unison_Status_" + id + extension;
        }

        private static string ExtensionFrom(HistoryStatus item, string uri)
        {
            if (!string.IsNullOrWhiteSpace(item?.MediaFileName))
            {
                string fromName = Path.GetExtension(item.MediaFileName);
                if (!string.IsNullOrWhiteSpace(fromName))
                {
                    return fromName;
                }
            }

            if (!string.IsNullOrWhiteSpace(uri))
            {
                try
                {
                    string path = uri;
                    int q = path.IndexOf('?');
                    if (q >= 0)
                    {
                        path = path.Substring(0, q);
                    }

                    string fromUri = Path.GetExtension(path);
                    if (!string.IsNullOrWhiteSpace(fromUri) && fromUri.Length <= 5)
                    {
                        return fromUri;
                    }
                }
                catch
                {
                }
            }

            string mime = (item?.MediaMimeType ?? string.Empty).ToLowerInvariant();
            if (mime.IndexOf("video", StringComparison.Ordinal) >= 0 ||
                mime.IndexOf("mp4", StringComparison.Ordinal) >= 0)
            {
                return ".mp4";
            }

            if (mime.IndexOf("png", StringComparison.Ordinal) >= 0)
            {
                return ".png";
            }

            if (mime.IndexOf("webp", StringComparison.Ordinal) >= 0)
            {
                return ".webp";
            }

            if (item != null && item.Kind == ChatPreviewKind.Video)
            {
                return ".mp4";
            }

            return ".jpg";
        }

        private void LogStatus(string eventName, string details)
        {
            _diagnostics?.Write("status", eventName, details);
        }
    }
}
