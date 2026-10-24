using Unison.Core.Helpers;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PersistedAliasRestoreTests
    {
        private const string SelfPn = "5511999999999@s.whatsapp.net";
        private const string SelfLid = "100200300@lid";
        private const string ContactPn = "5511888888888@s.whatsapp.net";
        private const string ContactLid = "400500600@lid";

        private static JidAliasTable Bound()
        {
            var table = new JidAliasTable();
            table.BindSelf(() => SelfPn, () => SelfLid);
            return table;
        }

        [Fact]
        public void Keeps_a_harmless_contact_pair()
        {
            PersistedAliasRestoreAction action = PersistedAliasRestore.Evaluate(
                ContactLid,
                ContactPn,
                Bound(),
                out string key,
                out string value);

            Assert.Equal(PersistedAliasRestoreAction.Keep, action);
            Assert.Equal(ContactLid, key);
            Assert.Equal(ContactPn, value);
        }

        [Fact]
        public void Skips_blank_sides()
        {
            Assert.Equal(
                PersistedAliasRestoreAction.Skip,
                PersistedAliasRestore.Evaluate("  ", ContactPn, Bound(), out _, out _));
            Assert.Equal(
                PersistedAliasRestoreAction.Skip,
                PersistedAliasRestore.Evaluate(ContactLid, null, Bound(), out _, out _));
        }

        [Fact]
        public void Drops_poison_that_redefines_self()
        {
            Assert.Equal(
                PersistedAliasRestoreAction.DropPoison,
                PersistedAliasRestore.Evaluate(
                    SelfLid,
                    ContactPn,
                    Bound(),
                    out _,
                    out _));
        }

        [Fact]
        public void Normalizes_before_keep()
        {
            PersistedAliasRestoreAction action = PersistedAliasRestore.Evaluate(
                "  " + ContactLid + "  ",
                "  " + ContactPn + "  ",
                Bound(),
                out string key,
                out string value);

            Assert.Equal(PersistedAliasRestoreAction.Keep, action);
            Assert.Equal(ContactLid, key);
            Assert.Equal(ContactPn, value);
        }
    }
}
