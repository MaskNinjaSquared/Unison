using System;
using System.Collections.Generic;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class UnloadMessageCacheIfInactiveDispatchTests
    {
        [Fact]
        public void Blank_or_active_is_noop()
        {
            var host = new FakeHost { Active = "a@x" };
            UnloadMessageCacheIfInactiveDispatch.Run(host, null);
            UnloadMessageCacheIfInactiveDispatch.Run(host, "a@x");
            Assert.Empty(host.Removed);
        }

        [Fact]
        public void Removes_normalized_and_raw_when_different()
        {
            var host = new FakeHost
            {
                NormalizeMap = { ["lid:1"] = "1@s.whatsapp.net" }
            };
            UnloadMessageCacheIfInactiveDispatch.Run(host, "lid:1");
            Assert.Contains("1@s.whatsapp.net", host.Removed);
            Assert.Contains("lid:1", host.Removed);
        }

        private sealed class FakeHost : IUnloadMessageCacheIfInactiveHost
        {
            public string Active;
            public Dictionary<string, string> NormalizeMap =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Removed = new List<string>();

            public bool IsActiveChatJid(string jid) =>
                string.Equals(Active, jid, StringComparison.OrdinalIgnoreCase);

            public string NormalizeJid(string jid) =>
                NormalizeMap.TryGetValue(jid, out var n) ? n : jid;

            public void RemoveMessagesByChatKey(string key) => Removed.Add(key);
        }
    }
}
