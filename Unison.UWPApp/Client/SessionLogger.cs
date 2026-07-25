using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Unison.UWPApp.Client
{
    /// <summary>
    /// Session logging levels
    /// </summary>
    public enum LogLevel
    {
        Trace,
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// Thread-safe session logger for debugging WhatsApp protocol traffic.
    /// Captures raw bytes, decoded messages, and protocol events.
    /// </summary>
    public class SessionLogger
    {
        private static SessionLogger _instance;
        private static readonly object _lock = new object();
        
        private readonly List<LogEntry> _entries = new List<LogEntry>();
        private readonly StringBuilder _textBuffer = new StringBuilder();
        private int _maxEntries = 10000;
        
        public event EventHandler<string> OnLogUpdated;
        
        public static SessionLogger Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SessionLogger();
                    }
                }
                return _instance;
            }
        }
        
        private const string SYNC_LOGGING_KEY = "PersistentSessionLoggingEnabled";

        public bool Enabled
        {
            get
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.ContainsKey(SYNC_LOGGING_KEY))
                {
                    return (bool)settings.Values[SYNC_LOGGING_KEY];
                }
                return false; // Default
            }
            set
            {
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values[SYNC_LOGGING_KEY] = value;
            }
        }
        
        public int EntryCount => _entries.Count;
        
        /// <summary>
        /// Log incoming bytes from WebSocket
        /// </summary>
        public void LogIn(byte[] data, string description = null)
        {
            if (!Enabled) return;
            
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Direction = "IN",
                Level = LogLevel.Trace,
                Data = data,
                Description = description
            };
            
            AddEntry(entry);
        }
        
        /// <summary>
        /// Log outgoing bytes to WebSocket
        /// </summary>
        public void LogOut(byte[] data, string description = null)
        {
            if (!Enabled) return;
            
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Direction = "OUT",
                Level = LogLevel.Trace,
                Data = data,
                Description = description
            };
            
            AddEntry(entry);
        }
        
        /// <summary>
        /// Log debug message
        /// </summary>
        public void Debug(string message, Dictionary<string, object> attrs = null)
        {
            if (!Enabled) return;
            
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = LogLevel.Debug,
                Message = message,
                Attributes = attrs
            };
            
            AddEntry(entry);
        }
        
        /// <summary>
        /// Log trace message (most verbose)
        /// </summary>
        public void Trace(string message, Dictionary<string, object> attrs = null)
        {
            if (!Enabled) return;
            
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = LogLevel.Trace,
                Message = message,
                Attributes = attrs
            };
            
            AddEntry(entry);
        }
        
        /// <summary>
        /// Log info message
        /// </summary>
        public void Info(string message)
        {
            if (!Enabled) return;
            
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = LogLevel.Info,
                Message = message
            };
            
            AddEntry(entry);
        }
        
        /// <summary>
        /// Log error message
        /// </summary>
        public void Error(string message, Exception ex = null)
        {
            if (!Enabled) return;
            
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = LogLevel.Error,
                Message = message + (ex != null ? $"\n{ex.Message}" : "")
            };
            
            AddEntry(entry);
        }

        /// <summary>
        /// Log a plaintext payload (for debugging registration/handshake)
        /// </summary>
        public void LogPayload(string name, byte[] data, string extraInfo = null)
        {
            if (!Enabled) return;
            
            var sb = new StringBuilder();
            sb.AppendLine($"=== PAYLOAD: {name} ===");
            if (!string.IsNullOrEmpty(extraInfo))
            {
                sb.AppendLine(extraInfo);
            }
            sb.AppendLine($"Length: {data?.Length ?? 0} bytes");
            if (data != null && data.Length > 0)
            {
                sb.AppendLine($"Base64: {Convert.ToBase64String(data)}");
                sb.AppendLine("Hex dump:");
                sb.Append(FormatHexDump(data, 0, data.Length));
            }
            sb.AppendLine("=== END PAYLOAD ===");
            
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = LogLevel.Debug,
                Message = sb.ToString()
            };
            
            AddEntry(entry);
        }

        /// <summary>
        /// Log key/value pairs for debugging
        /// </summary>
        public void LogKeyInfo(string title, Dictionary<string, string> values)
        {
            if (!Enabled) return;
            
            var sb = new StringBuilder();
            sb.AppendLine($"=== {title} ===");
            foreach (var kv in values)
            {
                sb.AppendLine($"  {kv.Key}: {kv.Value}");
            }
            sb.AppendLine("==================");
            
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = LogLevel.Debug,
                Message = sb.ToString()
            };
            
            AddEntry(entry);
        }
        
        
        private void AddEntry(LogEntry entry)
        {
            lock (_lock)
            {
                // Limit entries to prevent memory issues
                if (_entries.Count >= _maxEntries)
                {
                    _entries.RemoveAt(0);
                }
                
                _entries.Add(entry);
                
                // Build text representation
                var line = FormatEntry(entry);
                _textBuffer.AppendLine(line);
                
                // Notify UI
                OnLogUpdated?.Invoke(this, line);
            }
        }
        
        private string FormatEntry(LogEntry entry)
        {
            var sb = new StringBuilder();
            var time = entry.Timestamp.ToString("HH:mm:ss.fff");
            
            if (!string.IsNullOrEmpty(entry.Direction))
            {
                // Byte data entry
                sb.Append($"[{entry.Direction}] [{time}]");
                if (!string.IsNullOrEmpty(entry.Description))
                {
                    sb.Append($" {entry.Description}");
                }
                sb.AppendLine();
                
                if (entry.Data != null && entry.Data.Length > 0)
                {
                    // Format hex dump (max 256 bytes shown inline)
                    var bytesToShow = Math.Min(entry.Data.Length, 256);
                    sb.Append(FormatHexDump(entry.Data, 0, bytesToShow));
                    if (entry.Data.Length > 256)
                    {
                        sb.AppendLine($"... ({entry.Data.Length - 256} more bytes)");
                    }
                }
            }
            else
            {
                // Message entry
                var levelStr = entry.Level.ToString().ToUpper();
                sb.Append($"[{time}] {levelStr}: {entry.Message}");
                
                if (entry.Attributes != null && entry.Attributes.Count > 0)
                {
                    foreach (var attr in entry.Attributes)
                    {
                        sb.AppendLine();
                        sb.Append($"    {attr.Key}: {FormatValue(attr.Value)}");
                    }
                }
            }
            
            return sb.ToString();
        }
        
        private string FormatHexDump(byte[] data, int offset, int length)
        {
            var sb = new StringBuilder();
            var end = Math.Min(offset + length, data.Length);
            
            for (int i = offset; i < end; i += 16)
            {
                // Hex part
                for (int j = 0; j < 16 && i + j < end; j++)
                {
                    sb.Append(data[i + j].ToString("X2"));
                    sb.Append(" ");
                }
                
                // Pad if less than 16 bytes
                var remaining = Math.Min(16, end - i);
                for (int j = remaining; j < 16; j++)
                {
                    sb.Append("   ");
                }
                
                sb.Append(" | ");
                
                // ASCII part
                for (int j = 0; j < 16 && i + j < end; j++)
                {
                    var b = data[i + j];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                
                sb.AppendLine();
            }
            
            return sb.ToString();
        }
        
        private string FormatValue(object value)
        {
            if (value == null) return "null";
            if (value is byte[] bytes)
                return $"[{bytes.Length} bytes]";
            if (value is IList<string> list)
                return $"[{string.Join(", ", list)}]";
            return value.ToString();
        }
        
        /// <summary>
        /// Get all log text
        /// </summary>
        public string GetLogText()
        {
            lock (_lock)
            {
                return _textBuffer.ToString();
            }
        }
        
        /// <summary>
        /// Clear all logs
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _textBuffer.Clear();
            }
        }
        
        /// <summary>
        /// Save log to file using FileSavePicker (full byte dumps, not truncated)
        /// </summary>
        public async Task<string> SaveToFileAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
                picker.FileTypeChoices.Add("Text File", new List<string>() { ".txt" });
                picker.SuggestedFileName = $"session_log_{DateTime.Now:yyyyMMdd_HHmmss}";
                
                var file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return "Cancelled";
                }
                
                // Generate full log with complete byte dumps
                var fullLog = GenerateFullLog();
                await FileIO.WriteTextAsync(file, fullLog);
                
                return file.Path;
            }
            catch (Exception ex)
            {
                return $"Error saving: {ex.Message}";
            }
        }

        /// <summary>
        /// Generate log with complete byte dumps (for file export)
        /// </summary>
        private string GenerateFullLog()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== UNISON SESSION LOG ===");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Total entries: {_entries.Count}");
                sb.AppendLine();

                foreach (var entry in _entries)
                {
                    var time = entry.Timestamp.ToString("HH:mm:ss.fff");
                    
                    if (!string.IsNullOrEmpty(entry.Direction))
                    {
                        // Byte data entry - FULL dump
                        sb.AppendLine($"[{entry.Direction}] [{time}] {entry.Data?.Length ?? 0} bytes");
                        
                        if (entry.Data != null && entry.Data.Length > 0)
                        {
                            sb.Append(FormatHexDump(entry.Data, 0, entry.Data.Length));
                        }
                        sb.AppendLine();
                    }
                    else
                    {
                        // Message entry
                        var levelStr = entry.Level.ToString().ToUpper();
                        sb.AppendLine($"[{time}] {levelStr}: {entry.Message}");
                        
                        if (entry.Attributes != null)
                        {
                            foreach (var attr in entry.Attributes)
                            {
                                sb.AppendLine($"    {attr.Key}: {FormatValue(attr.Value)}");
                            }
                        }
                    }
                }
                
                return sb.ToString();
            }
        }
    }
    
    /// <summary>
    /// Single log entry
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Direction { get; set; }  // "IN", "OUT", or null for messages
        public LogLevel Level { get; set; }
        public byte[] Data { get; set; }
        public string Message { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Attributes { get; set; }
    }
}
