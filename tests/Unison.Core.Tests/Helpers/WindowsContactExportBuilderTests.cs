// =============================================================================
// WindowsContactExportBuilder — People export rows from a chat snapshot.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class WindowsContactExportBuilderTests
    {
        [Fact]
        public void Build_skips_groups_and_dedupes_by_canonical()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem
                {
                    JID = "5511988887777@s.whatsapp.net",
                    Name = "Ana",
                    IsGroup = false
                },
                new ChatItem
                {
                    JID = "120363@g.us",
                    Name = "Família",
                    IsGroup = true
                },
                new ChatItem
                {
                    JID = "5511988887777@s.whatsapp.net",
                    Name = "Ana Dup",
                    IsGroup = false
                }
            };

            List<AppContactExport> exports = WindowsContactExportBuilder.Build(
                chats,
                jids: null,
                isSelfJid: _ => false,
                tryPerson: _ => null,
                resolvePhone: jid => "5511988887777");

            Assert.Single(exports);
            Assert.Equal("5511988887777@s.whatsapp.net", exports[0].RemoteId);
            Assert.Equal("Ana", exports[0].DisplayName);
            Assert.Equal("5511988887777", exports[0].PhoneDigits);
        }

        [Fact]
        public void ShouldExportDirectChat_rejects_self_and_broadcast()
        {
            Assert.False(WindowsContactExportBuilder.ShouldExportDirectChat(
                new ChatItem { JID = "status@broadcast", IsGroup = false },
                isSelfJid: _ => false));

            Assert.False(WindowsContactExportBuilder.ShouldExportDirectChat(
                new ChatItem { JID = "5511999@s.whatsapp.net", IsGroup = false },
                isSelfJid: _ => true));
        }
    }
}
