using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupCreatedBySelfTests
    {
        private const string SelfPn = "5511999999999@s.whatsapp.net";
        private const string SelfLid = "123456789012345@lid";

        private static bool IsSelf(string jid) =>
            string.Equals(jid, SelfPn, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(jid, SelfLid, StringComparison.OrdinalIgnoreCase);

        [Fact]
        public void Returns_null_when_owner_is_not_self()
        {
            Assert.Null(GroupCreatedBySelf.TryFormat(
                "someone@s.whatsapp.net",
                null,
                1_700_000_000,
                IsSelf,
                "Created by you on {0}"));
        }

        [Fact]
        public void Formats_when_owner_pn_is_self()
        {
            string text = GroupCreatedBySelf.TryFormat(
                SelfLid,
                SelfPn,
                1_700_000_000,
                IsSelf,
                "Grupo criado por você em {0}");

            Assert.StartsWith("Grupo criado por você em ", text);
            Assert.DoesNotContain("{0}", text);
        }

        [Fact]
        public void Formats_when_only_owner_jid_is_self()
        {
            string text = GroupCreatedBySelf.TryFormat(
                SelfPn,
                null,
                1_700_000_000,
                IsSelf,
                "Group created by you on {0}");

            Assert.NotNull(text);
            Assert.Contains("Group created by you on ", text);
        }

        [Fact]
        public void Returns_null_when_creation_timestamp_is_missing()
        {
            Assert.Null(GroupCreatedBySelf.TryFormat(
                SelfPn,
                null,
                0,
                IsSelf,
                "Created by you on {0}"));
        }
    }
}
