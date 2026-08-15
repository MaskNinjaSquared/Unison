using Unison.Core.Contracts;
using Unison.Uwp.Helpers;

namespace Unison.Uwp.Services
{
    public sealed class StringResourcesService : IStringResources
    {
        public string Get(string key, string fallback = null) => LocalizedStrings.Get(key, fallback);
    }
}
