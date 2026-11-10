// =============================================================================
// FileStampIndex
//
// File name -> last write time for one folder, held in RAM so a per-row lookup
// does not stat the disk. Loaded once from a directory listing (off the UI);
// until then it does not answer and the caller checks the disk itself. Saves
// and deletes made while the listing is still running win over the listing.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.State
{
    public sealed class FileStampIndex
    {
        public struct Stamp
        {
            public Stamp(bool exists, DateTime writtenUtc)
            {
                Exists = exists;
                WrittenUtc = writtenUtc;
            }

            public bool Exists { get; }

            public DateTime WrittenUtc { get; }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, DateTime> _files = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime?> _earlyWrites = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        public bool IsLoaded
        {
            get { lock (_gate) { return _loaded; } }
        }

        public int Count
        {
            get { lock (_gate) { return _files.Count; } }
        }

        /// <summary>Null until <see cref="Load"/> ran: the caller must ask the disk.</summary>
        public Stamp? Lookup(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return new Stamp(false, DateTime.MinValue);
            }

            lock (_gate)
            {
                if (!_loaded)
                {
                    return null;
                }

                DateTime written;
                return _files.TryGetValue(fileName, out written)
                    ? new Stamp(true, written)
                    : new Stamp(false, DateTime.MinValue);
            }
        }

        public void Load(IEnumerable<KeyValuePair<string, DateTime>> listing)
        {
            lock (_gate)
            {
                _files.Clear();
                if (listing != null)
                {
                    foreach (KeyValuePair<string, DateTime> file in listing)
                    {
                        if (!string.IsNullOrEmpty(file.Key))
                        {
                            _files[file.Key] = file.Value;
                        }
                    }
                }

                foreach (KeyValuePair<string, DateTime?> write in _earlyWrites)
                {
                    if (write.Value.HasValue)
                    {
                        _files[write.Key] = write.Value.Value;
                    }
                    else
                    {
                        _files.Remove(write.Key);
                    }
                }

                _earlyWrites.Clear();
                _loaded = true;
            }
        }

        public void Set(string fileName, DateTime writtenUtc)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return;
            }

            lock (_gate)
            {
                if (_loaded)
                {
                    _files[fileName] = writtenUtc;
                }
                else
                {
                    _earlyWrites[fileName] = writtenUtc;
                }
            }
        }

        public void Remove(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return;
            }

            lock (_gate)
            {
                if (_loaded)
                {
                    _files.Remove(fileName);
                }
                else
                {
                    _earlyWrites[fileName] = null;
                }
            }
        }
    }
}
