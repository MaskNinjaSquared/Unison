using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Helpers;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// The one gate every media download waits on, including a picture that arrives live.
    /// </summary>
    public sealed class MediaDownloadGate
    {
        private readonly Func<bool> _isMobile;
        private readonly object _init = new object();
        private SemaphoreSlim _slots;

        public MediaDownloadGate(Func<bool> isMobile)
        {
            _isMobile = isMobile ?? throw new ArgumentNullException(nameof(isMobile));
        }

        public Task WaitAsync()
        {
            return Slots.WaitAsync();
        }

        public void Release()
        {
            Slots.Release();
        }

        private SemaphoreSlim Slots
        {
            get
            {
                if (_slots != null)
                {
                    return _slots;
                }

                lock (_init)
                {
                    if (_slots == null)
                    {
                        int slots = MediaDownloadSlots.ForDevice(_isMobile());
                        _slots = new SemaphoreSlim(slots, slots);
                    }
                }

                return _slots;
            }
        }
    }
}
