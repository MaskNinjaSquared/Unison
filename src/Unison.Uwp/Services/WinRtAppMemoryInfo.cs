using Unison.Core.Contracts;
using Windows.System;

namespace Unison.Uwp.Services
{
    /// <summary>WinRT <see cref="MemoryManager"/> behind <see cref="IAppMemoryInfo"/>.</summary>
    public sealed class WinRtAppMemoryInfo : IAppMemoryInfo
    {
        public string UsageLevel => MemoryManager.AppMemoryUsageLevel.ToString();

        public ulong UsageBytes => MemoryManager.AppMemoryUsage;

        public bool IsLow => MemoryManager.AppMemoryUsageLevel == AppMemoryUsageLevel.Low;
    }
}
