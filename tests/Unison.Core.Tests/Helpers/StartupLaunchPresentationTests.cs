using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class StartupLaunchPresentationTests
    {
        [Theory]
        [InlineData(StartupLaunchState.Enabled, true)]
        [InlineData(StartupLaunchState.EnabledByPolicy, true)]
        [InlineData(StartupLaunchState.Disabled, false)]
        [InlineData(StartupLaunchState.DisabledByUser, false)]
        [InlineData(StartupLaunchState.DisabledByPolicy, false)]
        [InlineData(StartupLaunchState.NotSupported, false)]
        public void Toggle_is_on_only_when_windows_will_launch_the_app(StartupLaunchState state, bool expected)
        {
            Assert.Equal(expected, StartupLaunchPresentation.IsOn(state));
        }

        [Theory]
        [InlineData(StartupLaunchState.Enabled, true)]
        [InlineData(StartupLaunchState.Disabled, true)]
        [InlineData(StartupLaunchState.DisabledByUser, false)]
        [InlineData(StartupLaunchState.DisabledByPolicy, false)]
        [InlineData(StartupLaunchState.EnabledByPolicy, false)]
        [InlineData(StartupLaunchState.NotSupported, false)]
        public void Only_states_the_app_can_change_leave_the_toggle_enabled(StartupLaunchState state, bool expected)
        {
            Assert.Equal(expected, StartupLaunchPresentation.CanToggle(state));
        }

        [Fact]
        public void Disabled_by_user_points_to_task_manager()
        {
            Assert.Equal(
                StartupLaunchPresentation.DisabledByUserHintKey,
                StartupLaunchPresentation.HintKey(StartupLaunchState.DisabledByUser));
        }

        [Theory]
        [InlineData(StartupLaunchState.DisabledByPolicy)]
        [InlineData(StartupLaunchState.EnabledByPolicy)]
        public void Policy_states_say_an_administrator_decides(StartupLaunchState state)
        {
            Assert.Equal(StartupLaunchPresentation.PolicyHintKey, StartupLaunchPresentation.HintKey(state));
        }

        [Theory]
        [InlineData(StartupLaunchState.Enabled)]
        [InlineData(StartupLaunchState.Disabled)]
        [InlineData(StartupLaunchState.NotSupported)]
        public void Changeable_states_have_no_hint(StartupLaunchState state)
        {
            Assert.Null(StartupLaunchPresentation.HintKey(state));
        }
    }
}
