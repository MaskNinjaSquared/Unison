using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class SelfProfileStateTests
    {
        private readonly List<string> _persisted = new List<string>();
        private SelfProfileAccount _account = new SelfProfileAccount
        {
            Id = "5511999@s.whatsapp.net",
            Lid = "123@lid",
            CredentialsName = "Creds",
            Phone = "5511999",
            AvatarUrl = "ms-appdata:///auth.jpg"
        };

        private SelfProfileState Create()
        {
            var state = new SelfProfileState();
            state.Bind(() => _account, label => label == "5511999", url => _persisted.Add(url));
            return state;
        }

        [Fact]
        public void Before_binding_the_snapshot_is_an_empty_profile()
        {
            Profile p = new SelfProfileState().Snapshot();

            Assert.NotNull(p);
            Assert.Null(p.Id);
            Assert.Null(p.Name);
        }

        [Fact]
        public void Snapshot_falls_back_to_the_account_name_and_picture()
        {
            Profile p = Create().Snapshot();

            Assert.Equal("5511999@s.whatsapp.net", p.Id);
            Assert.Equal("123@lid", p.Lid);
            Assert.Equal("Creds", p.Name);
            Assert.Equal("5511999", p.Phone);
            Assert.Equal("ms-appdata:///auth.jpg", p.AvatarUrl);
        }

        [Fact]
        public void A_set_name_and_picture_win_over_the_account()
        {
            SelfProfileState state = Create();
            state.Name = "Tiago";
            state.AvatarUrl = "ms-appdata:///fresh.jpg";

            Profile p = state.Snapshot();

            Assert.Equal("Tiago", p.Name);
            Assert.Equal("ms-appdata:///fresh.jpg", p.AvatarUrl);
        }

        [Fact]
        public void The_own_phone_digits_are_never_kept_as_a_name()
        {
            SelfProfileState state = Create();
            state.Name = "Tiago";

            state.Name = "5511999";

            Assert.Null(state.Name);
        }

        [Fact]
        public void Changed_fires_only_when_a_value_actually_changes()
        {
            SelfProfileState state = Create();
            int raised = 0;
            state.Changed += (s, e) => raised++;

            state.Name = "Tiago";
            state.Name = "Tiago";
            state.AvatarUrl = "a";
            state.AvatarUrl = "a";

            Assert.Equal(2, raised);
        }

        [Fact]
        public void Setting_the_picture_persists_it_but_restoring_does_not()
        {
            SelfProfileState state = Create();
            int raised = 0;
            state.Changed += (s, e) => raised++;

            state.RestoreAvatar("restored");
            state.AvatarUrl = "fresh";

            Assert.Equal(new[] { "fresh" }, _persisted);
            Assert.Equal(2, raised);
        }

        [Fact]
        public void A_failing_listener_does_not_break_the_writer()
        {
            SelfProfileState state = Create();
            state.Changed += (s, e) => throw new System.InvalidOperationException("boom");

            state.Name = "Tiago";

            Assert.Equal("Tiago", state.Name);
        }
    }
}
