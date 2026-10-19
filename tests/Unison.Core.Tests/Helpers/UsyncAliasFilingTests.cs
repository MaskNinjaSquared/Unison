using Unison.Core.Helpers;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class UsyncAliasFilingTests
    {
        private const string Pn = "5511999990001@s.whatsapp.net";
        private const string Lid = "222@lid";

        private readonly JidAliasTable _aliases = new JidAliasTable();

        private UsyncAliasFiling Build() => new UsyncAliasFiling(_aliases);

        [Fact]
        public void A_new_pair_is_filed_both_ways()
        {
            UsyncAliasFilingResult result = Build().TryFile(Pn, Lid);

            Assert.True(result.Filed);
            Assert.False(result.WasAlreadyAliased);
            Assert.Equal(Pn, _aliases[Lid]);
            Assert.Equal(Lid, _aliases[Pn]);
            Assert.Equal(Lid, result.AcceptedLid);
            Assert.Equal(Pn, result.AcceptedPn);
        }

        [Fact]
        public void An_existing_lid_is_reported_as_already_aliased()
        {
            _aliases[Lid] = Pn;
            _aliases[Pn] = Lid;

            UsyncAliasFilingResult result = Build().TryFile(Pn, Lid);

            Assert.True(result.WasAlreadyAliased);
        }

        [Fact]
        public void A_malformed_pair_is_refused()
        {
            UsyncAliasFilingResult result = Build().TryFile(Pn, "not-a-lid");

            Assert.False(result.Filed);
            Assert.Empty(_aliases.Snapshot());
        }

        [Fact]
        public void Blank_input_is_refused()
        {
            Assert.False(Build().TryFile(null, Lid).Filed);
            Assert.False(Build().TryFile(Pn, " ").Filed);
        }
    }
}
