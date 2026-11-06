// =============================================================================
// GroupCreation — create, then name / picture / disappearing timer.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts;
using Unison.Core.Helpers.Groups;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Groups;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class GroupCreationTests
    {
        private const string NewGroup = "120363000000000002@g.us";

        [Fact]
        public async Task Group_is_created_named_and_gets_picture_then_timer()
        {
            var wire = new FakeWire { CreatedJid = NewGroup };
            var named = new List<string>();
            var creation = new GroupCreation(
                wire,
                (jid, subject) => { named.Add(jid + "=" + subject); return Task.CompletedTask; },
                new FakeEncoder(new byte[] { 0xFF, 0xD8 }));

            GroupCreateResult result = await creation.CreateGroupAsync(new GroupCreateRequest
            {
                Subject = "  Família  ",
                ParticipantJids = new[] { "1@s.whatsapp.net", "1@s.whatsapp.net", " ", "2@lid" },
                PhotoBytes = new byte[] { 1, 2, 3 },
                Ephemeral = GroupEphemeralDuration.Days7
            });

            Assert.True(result.Succeeded);
            Assert.False(result.HasPartialFailure);
            Assert.Equal(NewGroup, result.GroupJid);
            Assert.Equal("Família", wire.Subject);
            Assert.Equal(new[] { "1@s.whatsapp.net", "2@lid" }, wire.Participants);
            Assert.Equal(new[] { NewGroup + "=Família" }, named);
            Assert.Equal(new[] { "picture:" + NewGroup, "ephemeral:" + NewGroup + ":604800" }, wire.FollowUps);
        }

        [Fact]
        public async Task No_photo_and_timer_off_send_no_follow_ups()
        {
            var wire = new FakeWire { CreatedJid = NewGroup };
            var creation = new GroupCreation(wire, (j, s) => Task.CompletedTask);

            GroupCreateResult result = await creation.CreateGroupAsync(new GroupCreateRequest
            {
                Subject = "G",
                ParticipantJids = new[] { "1@s.whatsapp.net" }
            });

            Assert.True(result.Succeeded);
            Assert.Empty(wire.FollowUps);
        }

        [Fact]
        public async Task Empty_subject_is_refused_before_the_wire()
        {
            var wire = new FakeWire { CreatedJid = NewGroup };
            var creation = new GroupCreation(wire, (j, s) => Task.CompletedTask);

            GroupCreateResult result = await creation.CreateGroupAsync(new GroupCreateRequest
            {
                Subject = "   ",
                ParticipantJids = new[] { "1@s.whatsapp.net" }
            });

            Assert.Equal(GroupCreateResult.ReasonEmptySubject, result.FailureReason);
            Assert.Null(wire.Subject);
        }

        [Fact]
        public async Task Group_without_members_is_created_with_only_us()
        {
            var wire = new FakeWire { CreatedJid = NewGroup };
            var creation = new GroupCreation(wire, (j, s) => Task.CompletedTask);

            GroupCreateResult result = await creation.CreateGroupAsync(new GroupCreateRequest { Subject = "G" });

            Assert.True(result.Succeeded);
            Assert.Equal("G", wire.Subject);
            Assert.Empty(wire.Participants);
        }

        [Fact]
        public async Task Offline_is_reported_as_not_connected()
        {
            var wire = new FakeWire { CreatedJid = NewGroup, Connected = false };
            var creation = new GroupCreation(wire, (j, s) => Task.CompletedTask);

            GroupCreateResult result = await creation.CreateGroupAsync(new GroupCreateRequest
            {
                Subject = "G",
                ParticipantJids = new[] { "1@s.whatsapp.net" }
            });

            Assert.Equal(GroupCreateResult.ReasonNotConnected, result.FailureReason);
        }

        [Fact]
        public async Task Refused_create_carries_the_reason_and_no_jid()
        {
            var wire = new FakeWire { CreateError = new InvalidOperationException("406") };
            var creation = new GroupCreation(wire, (j, s) => Task.CompletedTask);

            GroupCreateResult result = await creation.CreateGroupAsync(new GroupCreateRequest
            {
                Subject = "G",
                ParticipantJids = new[] { "1@s.whatsapp.net" }
            });

            Assert.False(result.Succeeded);
            Assert.Equal("406", result.FailureReason);
        }

        [Fact]
        public async Task Undecodable_photo_keeps_the_group_and_flags_it()
        {
            var wire = new FakeWire { CreatedJid = NewGroup };
            var creation = new GroupCreation(wire, (j, s) => Task.CompletedTask, new FakeEncoder(null));

            GroupCreateResult result = await creation.CreateGroupAsync(new GroupCreateRequest
            {
                Subject = "G",
                ParticipantJids = new[] { "1@s.whatsapp.net" },
                PhotoBytes = new byte[] { 9 },
                Ephemeral = GroupEphemeralDuration.Hours24
            });

            Assert.True(result.Succeeded);
            Assert.True(result.PhotoFailed);
            Assert.False(result.EphemeralFailed);
            Assert.Equal(new[] { "ephemeral:" + NewGroup + ":86400" }, wire.FollowUps);
        }

        [Fact]
        public async Task Refused_timer_keeps_the_group_and_flags_it()
        {
            var wire = new FakeWire { CreatedJid = NewGroup, EphemeralError = new InvalidOperationException("403") };
            var creation = new GroupCreation(wire, (j, s) => Task.CompletedTask);

            GroupCreateResult result = await creation.CreateGroupAsync(new GroupCreateRequest
            {
                Subject = "G",
                ParticipantJids = new[] { "1@s.whatsapp.net" },
                Ephemeral = GroupEphemeralDuration.Days90
            });

            Assert.True(result.Succeeded);
            Assert.True(result.EphemeralFailed);
        }

        [Fact]
        public async Task Community_is_created_with_trimmed_description_and_picture()
        {
            var wire = new FakeWire { CreatedJid = NewGroup };
            var creation = new GroupCreation(wire, (j, s) => Task.CompletedTask, new FakeEncoder(new byte[] { 1 }));

            GroupCreateResult result = await creation.CreateCommunityAsync(new CommunityCreateRequest
            {
                Subject = "Bairro",
                Description = "  Avisos  ",
                PhotoBytes = new byte[] { 2 }
            });

            Assert.True(result.Succeeded);
            Assert.Equal("Bairro", wire.Subject);
            Assert.Equal("Avisos", wire.Description);
            Assert.Equal(new[] { "picture:" + NewGroup }, wire.FollowUps);
        }

        [Fact]
        public void Ephemeral_presets_map_to_rc14_seconds()
        {
            Assert.Equal(0, GroupEphemeralDuration.Off.ToSeconds());
            Assert.Equal(86400, GroupEphemeralDuration.Hours24.ToSeconds());
            Assert.Equal(604800, GroupEphemeralDuration.Days7.ToSeconds());
            Assert.Equal(7776000, GroupEphemeralDuration.Days90.ToSeconds());
        }

        [Theory]
        [InlineData(GroupCreateResult.ReasonEmptySubject, false, "CreateGroup_Error_NameRequired")]
        [InlineData(GroupCreateResult.ReasonEmptySubject, true, "CreateCommunity_Error_NameRequired")]
        [InlineData(GroupCreateResult.ReasonNotConnected, true, "CreateFlow_Error_NotConnected")]
        [InlineData("406", false, "CreateGroup_Error_Failed")]
        [InlineData(null, true, "CreateCommunity_Error_Failed")]
        public void Failure_reason_picks_the_form_message(string reason, bool isCommunity, string expectedKey)
        {
            GroupCreateFailureText.Resolve(reason, isCommunity, out string key, out string fallback);

            Assert.Equal(expectedKey, key);
            Assert.False(string.IsNullOrWhiteSpace(fallback));
        }

        private sealed class FakeEncoder : IProfilePictureEncoder
        {
            private readonly byte[] _output;

            public FakeEncoder(byte[] output)
            {
                _output = output;
            }

            public Task<byte[]> EncodeSquareJpegAsync(byte[] image) => Task.FromResult(_output);
        }

        private sealed class FakeWire : IGroupWireActions
        {
            public bool Connected { get; set; } = true;

            public string CreatedJid { get; set; }

            public Exception CreateError { get; set; }

            public Exception EphemeralError { get; set; }

            public string Subject { get; private set; }

            public string Description { get; private set; }

            public IReadOnlyList<string> Participants { get; private set; }

            public List<string> FollowUps { get; } = new List<string>();

            public bool IsHandshakeComplete => Connected;

            public Task<string> CreateGroupAsync(string subject, IReadOnlyList<string> participantJids)
            {
                if (CreateError != null)
                {
                    return Task.FromException<string>(CreateError);
                }

                Subject = subject;
                Participants = participantJids;
                return Task.FromResult(CreatedJid);
            }

            public Task<string> CreateCommunityAsync(string subject, string description)
            {
                if (CreateError != null)
                {
                    return Task.FromException<string>(CreateError);
                }

                Subject = subject;
                Description = description;
                return Task.FromResult(CreatedJid);
            }

            public Task UpdateGroupEphemeralAsync(string groupJid, int seconds)
            {
                if (EphemeralError != null)
                {
                    return Task.FromException(EphemeralError);
                }

                FollowUps.Add("ephemeral:" + groupJid + ":" + seconds);
                return Task.CompletedTask;
            }

            public Task UpdateGroupPictureAsync(string groupJid, byte[] jpeg)
            {
                FollowUps.Add("picture:" + groupJid);
                return Task.CompletedTask;
            }

            public Task<BinaryNode> QueryGroupMetadataAsync(string groupJid, CancellationToken cancellationToken = default) =>
                Task.FromResult<BinaryNode>(null);

            public Task<BinaryNode> QueryParticipatingGroupsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<BinaryNode>(null);

            public Task<GroupProfilePictureResult> GetPreviewPictureAsync(string jid) =>
                Task.FromResult<GroupProfilePictureResult>(null);

            public Task LeaveGroupAsync(string groupJid) => Task.CompletedTask;

            public Task<IReadOnlyList<CommunityLinkedGroupSnapshot>> FetchCommunityLinkedGroupsAsync(
                string communityOrGroupJid,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<CommunityLinkedGroupSnapshot>>(Array.Empty<CommunityLinkedGroupSnapshot>());

            public Task<bool> JoinGroupAsync(
                string groupJid,
                string inviteCode = null,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(false);
        }
    }
}
