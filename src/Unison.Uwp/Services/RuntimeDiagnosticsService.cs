using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unison.Background;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Uwp.Transport;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Lightweight always-on runtime journal for the refactor baseline.
    ///
    /// Design goals for 512 MB phones:
    /// - callers never wait for disk I/O;
    /// - bounded in-memory queues;
    /// - no message text, contact names or raw protocol payloads;
    /// - small rotating files in LocalState;
    /// - periodic snapshots that make deadlocks and silent sockets measurable.
    /// </summary>
    public sealed class RuntimeDiagnosticsService : IRuntimeDiagnostics
    {
        private const string DiagnosticsFolderName = "Diagnostics";
        private const string CurrentLogName = "runtime-current.log";
        private const string PreviousLogName = "runtime-previous.log";
        private const int MaxPendingLines = 240;
        private const int MaxRecentLines = 180;
        private const int FlushBatchSize = 120;
        private const ulong MaxLogBytes = 768UL * 1024UL;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(30);

        private static readonly Lazy<RuntimeDiagnosticsService> LazyInstance =
            new Lazy<RuntimeDiagnosticsService>(() => new RuntimeDiagnosticsService());

        private readonly ConcurrentQueue<string> _pendingLines = new ConcurrentQueue<string>();
        private readonly object _recentLock = new object();
        private readonly Queue<string> _recentLines = new Queue<string>();
        private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
        private readonly string _runId = Guid.NewGuid().ToString("N").Substring(0, 8);
        private Timer _flushTimer;
        private Timer _healthTimer;
        private int _pendingCount;
        private int _started;
        private int _healthSamplingStarted;
        private int _flushRequested;
        private int _droppedLineCount;
        private string _lastHealthFingerprint;
        private IWhatsAppService _whatsAppService;

        public static RuntimeDiagnosticsService Instance
        {
            get { return LazyInstance.Value; }
        }

        private RuntimeDiagnosticsService()
        {
        }

        /// <summary>
        /// Wires the WhatsApp service used by health snapshots (avoids static Instance lookup).
        /// </summary>
        public void AttachWhatsAppService(IWhatsAppService whatsAppService)
        {
            _whatsAppService = whatsAppService;
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            try
            {
                TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            }
            catch
            {
            }

            _flushTimer = new Timer(FlushTimerCallback, null, FlushInterval, FlushInterval);
            Write("lifecycle", "runtime-journal-start", BuildEnvironmentLine());
        }

        public void StartHealthSampling()
        {
            if (Interlocked.Exchange(ref _healthSamplingStarted, 1) != 0)
            {
                return;
            }

            _healthTimer = new Timer(HealthTimerCallback, null, TimeSpan.FromSeconds(8), HealthInterval);
            Write("health", "sampling-start", "intervalSeconds=" + (int)HealthInterval.TotalSeconds);
        }

        public void Write(string category, string eventName, string details = null)
        {
            try
            {
                string line = string.Format(
                    "{0:O}|run={1}|tid={2}|{3}|{4}|{5}",
                    DateTime.UtcNow,
                    _runId,
                    (Task.CurrentId ?? 0),
                    Clean(category),
                    Clean(eventName),
                    Clean(details));

                int count = Interlocked.Increment(ref _pendingCount);
                if (count > MaxPendingLines)
                {
                    Interlocked.Decrement(ref _pendingCount);
                    Interlocked.Increment(ref _droppedLineCount);
                    return;
                }

                _pendingLines.Enqueue(line);
                lock (_recentLock)
                {
                    _recentLines.Enqueue(line);
                    while (_recentLines.Count > MaxRecentLines)
                    {
                        _recentLines.Dequeue();
                    }
                }

                Debug.WriteLine("[Runtime] " + line);
                if (count >= 24 && Interlocked.Exchange(ref _flushRequested, 1) == 0)
                {
                    _ = FlushAsync("threshold");
                }
            }
            catch
            {
                // Diagnostics must never interfere with the application.
            }
        }

        public void RecordException(string category, string eventName, Exception exception, string details = null)
        {
            string exceptionText = exception == null
                ? "<no exception>"
                : exception.GetType().FullName + ": " + exception.Message + " | " + exception.StackTrace;
            Write(category, eventName, string.IsNullOrWhiteSpace(details)
                ? exceptionText
                : details + " | " + exceptionText);
        }

        public string GetRecentText()
        {
            lock (_recentLock)
            {
                return string.Join(Environment.NewLine, _recentLines.ToArray());
            }
        }

        public RuntimeDiagnosticsSnapshot CaptureSnapshot()
        {
            try
            {
                var whatsApp = _whatsAppService;
                if (whatsApp == null)
                {
                    return new RuntimeDiagnosticsSnapshot
                    {
                        CapturedUtc = DateTime.UtcNow,
                        ConnectionStatus = "whatsapp-not-attached"
                    };
                }

                return whatsApp.GetRuntimeDiagnosticsSnapshot();
            }
            catch (Exception ex)
            {
                RecordException("health", "snapshot-failed", ex);
                return new RuntimeDiagnosticsSnapshot
                {
                    CapturedUtc = DateTime.UtcNow,
                    ConnectionStatus = "snapshot-error"
                };
            }
        }

        public async Task FlushAsync(string reason)
        {
            if (!await _flushLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                Interlocked.Exchange(ref _flushRequested, 0);
                var batch = new List<string>(FlushBatchSize + 1);
                string line;
                while (batch.Count < FlushBatchSize && _pendingLines.TryDequeue(out line))
                {
                    Interlocked.Decrement(ref _pendingCount);
                    batch.Add(line);
                }

                int dropped = Interlocked.Exchange(ref _droppedLineCount, 0);
                if (dropped > 0)
                {
                    batch.Insert(0, string.Format(
                        "{0:O}|run={1}|tid={2}|runtime|lines-dropped|count={3}",
                        DateTime.UtcNow,
                        _runId,
                        (Task.CurrentId ?? 0),
                        dropped));
                }

                if (batch.Count == 0)
                {
                    return;
                }

                StorageFolder folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    DiagnosticsFolderName,
                    CreationCollisionOption.OpenIfExists);
                StorageFile current = await folder.CreateFileAsync(CurrentLogName, CreationCollisionOption.OpenIfExists);
                await RotateIfNeededAsync(folder, current);
                current = await folder.CreateFileAsync(CurrentLogName, CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(current, string.Join(Environment.NewLine, batch) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Runtime] Flush failed (" + reason + "): " + ex.Message);
            }
            finally
            {
                _flushLock.Release();
            }

            if (Volatile.Read(ref _pendingCount) > 0 && Interlocked.Exchange(ref _flushRequested, 1) == 0)
            {
                _ = FlushAsync("remaining");
            }
        }

        public async Task<string> ExportReportAsync()
        {
            try
            {
                Write("diagnostics", "export-requested");
                await FlushAsync("export");

                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeChoices.Add("Text report", new List<string> { ".txt" });
                picker.SuggestedFileName = "unison_diagnostics_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                StorageFile destination = await picker.PickSaveFileAsync();
                if (destination == null)
                {
                    return "Cancelled";
                }

                string report = await BuildFullReportAsync();
                await FileIO.WriteTextAsync(destination, report);
                return destination.Path;
            }
            catch (Exception ex)
            {
                RecordException("diagnostics", "export-failed", ex);
                return "Error: " + ex.Message;
            }
        }

        public async Task ClearAsync()
        {
            try
            {
                string ignored;
                while (_pendingLines.TryDequeue(out ignored))
                {
                }
                Interlocked.Exchange(ref _pendingCount, 0);

                lock (_recentLock)
                {
                    _recentLines.Clear();
                }

                StorageFolder folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    DiagnosticsFolderName,
                    CreationCollisionOption.OpenIfExists);
                await DeleteIfExistsAsync(folder, CurrentLogName);
                await DeleteIfExistsAsync(folder, PreviousLogName);
                await DeleteRootFileIfExistsAsync(SocketBrokerConstants.BrokerLogFile);
                await BrokerFrameJournal.ClearAsync();
                Write("diagnostics", "logs-cleared");
            }
            catch (Exception ex)
            {
                RecordException("diagnostics", "clear-failed", ex);
            }
        }

        private async Task<string> BuildFullReportAsync()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== UNISON V6.7.3 RELIABLE OUT-OF-PROCESS BROKER DIAGNOSTICS ===");
            sb.AppendLine("Generated: " + DateTimeOffset.Now.ToString("O"));
            sb.AppendLine("Run id: " + _runId);
            sb.AppendLine(BuildEnvironmentLine());
            sb.AppendLine();
            sb.AppendLine(CaptureSnapshot().ToDisplayText());
            sb.AppendLine("=== PREVIOUS RUNTIME LOG ===");
            sb.AppendLine(await ReadLogFileAsync(PreviousLogName));
            sb.AppendLine("=== CURRENT RUNTIME LOG ===");
            sb.AppendLine(await ReadLogFileAsync(CurrentLogName));
            sb.AppendLine("=== SOCKET BROKER LOG ===");
            sb.AppendLine(await ReadRootLogFileAsync(SocketBrokerConstants.BrokerLogFile));
            sb.AppendLine("=== RECENT IN-MEMORY EVENTS ===");
            sb.AppendLine(GetRecentText());
            return sb.ToString();
        }

        private static async Task RotateIfNeededAsync(StorageFolder folder, StorageFile current)
        {
            try
            {
                var properties = await current.GetBasicPropertiesAsync();
                if (properties.Size < MaxLogBytes)
                {
                    return;
                }

                await DeleteIfExistsAsync(folder, PreviousLogName);
                await current.RenameAsync(PreviousLogName, NameCollisionOption.ReplaceExisting);
            }
            catch
            {
            }
        }

        private static async Task DeleteIfExistsAsync(StorageFolder folder, string fileName)
        {
            try
            {
                StorageFile file = await folder.GetFileAsync(fileName);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        private static async Task DeleteRootFileIfExistsAsync(string fileName)
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(fileName);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        private static async Task<string> ReadRootLogFileAsync(string fileName)
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(fileName);
                return await FileIO.ReadTextAsync(file);
            }
            catch
            {
                return "<not available>";
            }
        }

        private static async Task<string> ReadLogFileAsync(string fileName)
        {
            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    DiagnosticsFolderName,
                    CreationCollisionOption.OpenIfExists);
                StorageFile file = await folder.GetFileAsync(fileName);
                return await FileIO.ReadTextAsync(file);
            }
            catch
            {
                return "<not available>";
            }
        }

        private void FlushTimerCallback(object state)
        {
            _ = FlushAsync("timer");
        }

        private void HealthTimerCallback(object state)
        {
            try
            {
                RuntimeDiagnosticsSnapshot snapshot = CaptureSnapshot();
                string fingerprint = snapshot.ToCompactLine();
                bool changed = !string.Equals(_lastHealthFingerprint, fingerprint, StringComparison.Ordinal);
                bool important = snapshot.IsPotentiallyStalled || !snapshot.IsServiceConnected ||
                                 snapshot.LiveIncomingQueueDepth > 0 || snapshot.OfflineIncomingQueueDepth > 0 ||
                                 snapshot.SocketNodeQueueDepth > 0;

                if (changed || important)
                {
                    _lastHealthFingerprint = fingerprint;
                    Write("health", snapshot.IsPotentiallyStalled ? "snapshot-stalled" : "snapshot", fingerprint);
                }
            }
            catch (Exception ex)
            {
                RecordException("health", "timer-failed", ex);
            }
        }

        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            RecordException("runtime", "unobserved-task-exception", e.Exception);
            try
            {
                e.SetObserved();
            }
            catch
            {
            }
        }

        private static string BuildEnvironmentLine()
        {
            try
            {
                Package package = Package.Current;
                PackageVersion version = package.Id.Version;
                return string.Format(
                    "package={0}; version={1}.{2}.{3}.{4}; architecture={5}; memory={6}/{7}MB; level={8}",
                    package.Id.Name,
                    version.Major,
                    version.Minor,
                    version.Build,
                    version.Revision,
                    package.Id.Architecture,
                    MemoryManager.AppMemoryUsage / (1024UL * 1024UL),
                    MemoryManager.AppMemoryUsageLimit / (1024UL * 1024UL),
                    MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception ex)
            {
                return "environment-unavailable=" + ex.Message;
            }
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();
            return cleaned.Length <= 1200 ? cleaned : cleaned.Substring(0, 1200) + "...";
        }
    }
}
