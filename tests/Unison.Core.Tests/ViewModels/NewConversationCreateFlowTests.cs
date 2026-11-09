using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.Tests.Helpers;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public sealed class NewConversationCreateFlowTests
    {
        [Fact]
        public void NewGroup_opens_group_flow_on_members_step()
        {
            var vm = CreateVm();

            vm.NewGroupCommand.Execute(null);

            Assert.Equal(NewConversationCreateFlow.Group, vm.CreateFlow);
            Assert.Equal(0, vm.GroupStepIndex);
            Assert.Equal("New group", vm.HeaderTitle);
            Assert.Equal("Next", vm.PrimaryActionLabel);
            Assert.Equal("\uE72A", vm.PrimaryActionGlyph);
            Assert.Equal("\uE716", vm.CreateExplainGlyph);
        }

        [Fact]
        public void Group_next_moves_to_info_then_confirm_label()
        {
            var vm = CreateVm();
            vm.NewGroupCommand.Execute(null);

            vm.CreateNextCommand.Execute(null);

            Assert.Equal(1, vm.GroupStepIndex);
            Assert.Equal("Confirm", vm.PrimaryActionLabel);
            Assert.Equal("\uE73E", vm.PrimaryActionGlyph);
            Assert.Equal("\uE8F1", vm.CreateExplainGlyph);
        }

        [Fact]
        public void Group_confirm_creates_through_the_facade_and_opens_the_chat()
        {
            var groups = new FakeGroups
            {
                NextResult = new GroupCreateResult { GroupJid = "120363000000000003@g.us" }
            };
            var chats = new RecordingChats();
            var notifications = new RecordingNotifications();
            var vm = CreateVm(groups: groups, chats: chats, notifications: notifications);
            string opened = null;
            vm.ChatOpenRequested += (s, jid) => opened = jid;
            vm.Open();
            vm.NewGroupCommand.Execute(null);
            vm.SelectedMembers.Add(new ChatItem { JID = "a@s.whatsapp.net", Name = "A" });
            vm.CreateNextCommand.Execute(null);
            vm.GroupName = "Família";
            vm.GroupEphemeralDuration = GroupEphemeralDuration.Days90;

            vm.CreateNextCommand.Execute(null);

            Assert.True(SpinWait.SpinUntil(() => opened != null, TimeSpan.FromSeconds(2)));
            Assert.Equal("120363000000000003@g.us", opened);
            Assert.Equal("Família", groups.LastGroup.Subject);
            Assert.Equal(new[] { "a@s.whatsapp.net" }, groups.LastGroup.ParticipantJids);
            Assert.Equal(GroupEphemeralDuration.Days90, groups.LastGroup.Ephemeral);
            Assert.Equal(NewConversationCreateFlow.None, vm.CreateFlow);
            Assert.Equal("120363000000000003@g.us", chats.LastStartJid);
            Assert.Equal("Família", chats.LastStartDisplayName);
            Assert.True(SpinWait.SpinUntil(() => notifications.LastBody != null, TimeSpan.FromSeconds(2)));
            Assert.Equal("Group created", notifications.LastTitle);
            Assert.Equal("Família created successfully.", notifications.LastBody);
        }

        [Fact]
        public async Task Group_confirm_refused_shows_why_and_stays_on_info()
        {
            var dialogs = new FakeDialogs();
            var groups = new FakeGroups
            {
                NextResult = GroupCreateResult.Failed(GroupCreateResult.ReasonEmptySubject)
            };
            var vm = CreateVm(dialogs, groups: groups);
            vm.NewGroupCommand.Execute(null);
            vm.CreateNextCommand.Execute(null);

            vm.CreateNextCommand.Execute(null);
            await dialogs.WaitShownAsync();

            Assert.Equal("Enter a group name.", dialogs.LastContent);
            Assert.Equal(NewConversationCreateFlow.Group, vm.CreateFlow);
            Assert.Equal(1, vm.GroupStepIndex);
        }

        [Fact]
        public async Task Create_failure_dialog_is_shown_on_the_ui_dispatcher()
        {
            var dispatcher = new CountingDispatcher();
            var dialogs = new FakeDialogs { IsOnUi = () => dispatcher.IsRunningAction };
            var groups = new FakeGroups
            {
                NextResult = GroupCreateResult.Failed(GroupCreateResult.ReasonNotConnected)
            };
            var vm = CreateVm(dialogs, groups: groups, dispatcher: dispatcher);
            vm.NewCommunityCommand.Execute(null);
            vm.CommunityName = "Bairro";

            vm.CreateNextCommand.Execute(null);
            await dialogs.WaitShownAsync();

            Assert.True(dialogs.ShownOnUi);
        }

        [Fact]
        public void Group_confirm_without_members_still_creates()
        {
            var groups = new FakeGroups
            {
                NextResult = new GroupCreateResult { GroupJid = "120363000000000005@g.us" }
            };
            var vm = CreateVm(groups: groups);
            string opened = null;
            vm.ChatOpenRequested += (s, jid) => opened = jid;
            vm.Open();
            vm.NewGroupCommand.Execute(null);
            vm.CreateNextCommand.Execute(null);
            vm.GroupName = "Só eu";

            vm.CreateNextCommand.Execute(null);

            Assert.True(SpinWait.SpinUntil(() => opened != null, TimeSpan.FromSeconds(2)));
            Assert.Empty(groups.LastGroup.ParticipantJids);
        }

        [Fact]
        public void Group_confirm_dismisses_busy_on_urgent_dispatcher()
        {
            // Normal name-batch hops must not keep CreateBusy painted after wire OK (e7f675d0).
            var dispatcher = new CountingDispatcher();
            var groups = new FakeGroups
            {
                NextResult = new GroupCreateResult { GroupJid = "120363000000000015@g.us" }
            };
            var vm = CreateVm(groups: groups, dispatcher: dispatcher);
            string opened = null;
            vm.ChatOpenRequested += (s, jid) => opened = jid;
            vm.Open();
            vm.NewGroupCommand.Execute(null);
            vm.CreateNextCommand.Execute(null);
            vm.GroupName = "Urgent dismiss";

            vm.CreateNextCommand.Execute(null);

            Assert.True(SpinWait.SpinUntil(() => opened != null && !vm.IsCreating, TimeSpan.FromSeconds(2)));
            Assert.True(dispatcher.UrgentPasses > 0);
            Assert.False(vm.IsOpen);
        }

        [Fact]
        public async Task Confirm_blocks_the_button_and_shows_busy_until_the_facade_answers()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var groups = new FakeGroups
            {
                NextResult = new GroupCreateResult { GroupJid = "120363000000000007@g.us" },
                Gate = release.Task
            };
            var vm = CreateVm(groups: groups);
            vm.Open();
            vm.NewGroupCommand.Execute(null);
            vm.CreateNextCommand.Execute(null);
            vm.GroupName = "Busy";

            Assert.False(vm.IsCreating);
            Assert.True(vm.CreateNextCommand.CanExecute(null));

            vm.CreateNextCommand.Execute(null);

            Assert.True(SpinWait.SpinUntil(() => vm.IsCreating, TimeSpan.FromSeconds(2)));
            Assert.False(vm.CreateNextCommand.CanExecute(null));
            Assert.True(vm.TryHandleCreateBack());
            Assert.Equal(NewConversationCreateFlow.Group, vm.CreateFlow);

            release.TrySetResult(true);

            Assert.True(SpinWait.SpinUntil(() => !vm.IsCreating, TimeSpan.FromSeconds(2)));
            Assert.True(vm.CreateNextCommand.CanExecute(null) || vm.CreateFlow == NewConversationCreateFlow.None);
            await Task.CompletedTask;
        }

        [Fact]
        public void Community_confirm_creates_and_closes()
        {
            var groups = new FakeGroups
            {
                NextResult = new GroupCreateResult { GroupJid = "120363000000000004@g.us" }
            };
            var notifications = new RecordingNotifications();
            var chats = new RecordingChats();
            var vm = CreateVm(groups: groups, chats: chats, notifications: notifications);
            vm.Open();
            vm.NewCommunityCommand.Execute(null);
            vm.CommunityName = "Bairro";
            vm.CommunityDescription = "Avisos";

            vm.CreateNextCommand.Execute(null);

            Assert.True(SpinWait.SpinUntil(
                () => vm.CreateFlow == NewConversationCreateFlow.None,
                TimeSpan.FromSeconds(2)));
            Assert.True(SpinWait.SpinUntil(() => chats.LastCommunityJid != null, TimeSpan.FromSeconds(2)));
            Assert.Equal("120363000000000004@g.us", chats.LastCommunityJid);
            Assert.Equal("Bairro", chats.LastCommunityDisplayName);
            Assert.Null(chats.LastStartJid);
            Assert.Equal("Bairro", groups.LastCommunity.Subject);
            Assert.Equal("Avisos", groups.LastCommunity.Description);
            Assert.True(SpinWait.SpinUntil(() => groups.ForcedListings == 1, TimeSpan.FromSeconds(2)));
            Assert.True(SpinWait.SpinUntil(() => notifications.LastBody != null, TimeSpan.FromSeconds(2)));
            Assert.Equal("Community created", notifications.LastTitle);
            Assert.Equal("Bairro created successfully.", notifications.LastBody);
        }

        [Fact]
        public void Community_confirm_with_photo_passes_bytes_and_dismisses_on_urgent()
        {
            // Same CreateBusy starvation as group: picture follow-up + name batch must not hold
            // the overlay after wire create returns (e7f675d0 community-create timeout path).
            var dispatcher = new CountingDispatcher();
            var groups = new FakeGroups
            {
                NextResult = new GroupCreateResult { GroupJid = "120363000000000014@g.us" }
            };
            var chats = new RecordingChats();
            var vm = CreateVm(groups: groups, chats: chats, dispatcher: dispatcher);
            vm.Open();
            vm.NewCommunityCommand.Execute(null);
            vm.CommunityName = "Bairro";
            vm.CommunityPhotoBytes = new byte[] { 9, 8, 7 };

            vm.CreateNextCommand.Execute(null);

            Assert.True(SpinWait.SpinUntil(
                () => !vm.IsCreating && vm.CreateFlow == NewConversationCreateFlow.None,
                TimeSpan.FromSeconds(2)));
            Assert.Equal(new byte[] { 9, 8, 7 }, groups.LastCommunity.PhotoBytes);
            Assert.Equal("120363000000000014@g.us", chats.LastCommunityJid);
            Assert.True(dispatcher.UrgentPasses > 0);
            Assert.False(vm.IsOpen);
        }

        [Fact]
        public async Task Community_create_failure_clears_busy_on_urgent_before_dialog()
        {
            var dispatcher = new CountingDispatcher();
            var dialogs = new FakeDialogs { IsOnUi = () => dispatcher.IsRunningAction };
            var groups = new FakeGroups
            {
                NextResult = GroupCreateResult.Failed(GroupCreateResult.ReasonNotConnected)
            };
            var vm = CreateVm(dialogs, groups: groups, dispatcher: dispatcher);
            vm.Open();
            vm.NewCommunityCommand.Execute(null);
            vm.CommunityName = "Bairro";
            vm.CommunityPhotoBytes = new byte[] { 1 };

            vm.CreateNextCommand.Execute(null);
            await dialogs.WaitShownAsync().ConfigureAwait(true);

            Assert.False(vm.IsCreating);
            Assert.True(dispatcher.UrgentPasses > 0);
            Assert.True(dialogs.ShownOnUi);
            Assert.Equal(NewConversationCreateFlow.Community, vm.CreateFlow);
        }

        [Fact]
        public async Task Group_confirm_without_group_service_shows_not_implemented_and_stays_open()
        {
            var dialogs = new FakeDialogs();
            var vm = CreateVm(dialogs);
            vm.NewGroupCommand.Execute(null);
            vm.CreateNextCommand.Execute(null);

            vm.CreateNextCommand.Execute(null);
            await dialogs.WaitShownAsync();

            Assert.True(dialogs.MessageShown);
            Assert.Equal(NewConversationCreateFlow.Group, vm.CreateFlow);
            Assert.Equal(1, vm.GroupStepIndex);
        }

        [Fact]
        public void Group_back_from_info_returns_to_members()
        {
            var vm = CreateVm();
            vm.NewGroupCommand.Execute(null);
            vm.CreateNextCommand.Execute(null);

            Assert.True(vm.TryHandleCreateBack());
            Assert.Equal(0, vm.GroupStepIndex);
            Assert.Equal(NewConversationCreateFlow.Group, vm.CreateFlow);
        }

        [Fact]
        public void Group_back_from_members_returns_to_catalog()
        {
            var vm = CreateVm();
            vm.NewGroupCommand.Execute(null);

            Assert.True(vm.TryHandleCreateBack());
            Assert.Equal(NewConversationCreateFlow.None, vm.CreateFlow);
        }

        [Fact]
        public void Community_start_persists_across_layout_minimal_toggle()
        {
            var vm = CreateVm();
            vm.ReportLayoutMinimal(true);
            vm.NewCommunityCommand.Execute(null);
            Assert.True(vm.ShowCommunityIntro);

            vm.StartCommunityCreateCommand.Execute(null);
            Assert.False(vm.ShowCommunityIntro);

            vm.ReportLayoutMinimal(false);
            vm.ReportLayoutMinimal(true);
            Assert.False(vm.ShowCommunityIntro);
            Assert.Equal(NewConversationCreateFlow.Community, vm.CreateFlow);
        }

        [Fact]
        public void NewChannel_opens_channel_flow_with_confirm_chrome()
        {
            var vm = CreateVm();
            vm.ReportLayoutMinimal(false);

            vm.NewChannelCommand.Execute(null);

            Assert.Equal(NewConversationCreateFlow.Channel, vm.CreateFlow);
            Assert.True(vm.IsCreateChannel);
            Assert.True(vm.ShowCreateForm);
            Assert.False(vm.ShowChannelIntro);
            Assert.Equal("New channel", vm.HeaderTitle);
            Assert.Equal("Confirm", vm.PrimaryActionLabel);
            Assert.Equal("\uE73E", vm.PrimaryActionGlyph);
            Assert.Equal("\uE8BD", vm.CreateExplainGlyph);
            Assert.Equal(
                "Create a channel to reach an unlimited number of people.",
                vm.CreateExplainTitle);
        }

        [Fact]
        public void Channel_start_persists_across_layout_minimal_toggle()
        {
            var vm = CreateVm();
            vm.ReportLayoutMinimal(true);
            vm.NewChannelCommand.Execute(null);
            Assert.True(vm.ShowChannelIntro);

            vm.StartChannelCreateCommand.Execute(null);
            Assert.False(vm.ShowChannelIntro);
            Assert.True(vm.ShowCreateForm);

            vm.ReportLayoutMinimal(false);
            vm.ReportLayoutMinimal(true);
            Assert.False(vm.ShowChannelIntro);
            Assert.Equal(NewConversationCreateFlow.Channel, vm.CreateFlow);
        }

        [Fact]
        public void Channel_confirm_creates_through_the_facade_and_opens_the_channel()
        {
            const string channelJid = "120363000000000010@newsletter";
            var groups = new FakeGroups();
            var channels = new FakeChannels
            {
                NextResult = new ChannelCreateResult { ChannelJid = channelJid }
            };
            var chats = new RecordingChats();
            var notifications = new RecordingNotifications();
            var vm = CreateVm(groups: groups, chats: chats, notifications: notifications, channels: channels);
            string opened = null;
            vm.ChatOpenRequested += (s, jid) => opened = jid;
            vm.Open();
            vm.ReportLayoutMinimal(false);
            vm.NewChannelCommand.Execute(null);
            vm.ChannelName = "News";
            vm.ChannelDescription = "Updates";
            vm.ChannelPhotoBytes = new byte[] { 7 };

            vm.CreateNextCommand.Execute(null);

            Assert.True(SpinWait.SpinUntil(() => opened != null, TimeSpan.FromSeconds(2)));
            Assert.Equal(channelJid, opened);
            Assert.Equal("News", channels.LastRequest.Name);
            Assert.Equal("Updates", channels.LastRequest.Description);
            Assert.Equal(new byte[] { 7 }, channels.LastRequest.PhotoBytes);
            Assert.Null(groups.LastGroup);
            Assert.Null(groups.LastCommunity);
            Assert.Equal(channelJid, chats.LastStartJid);
            Assert.Equal("News", chats.LastStartDisplayName);
            Assert.Equal(NewConversationCreateFlow.None, vm.CreateFlow);
            Assert.True(SpinWait.SpinUntil(() => notifications.LastBody != null, TimeSpan.FromSeconds(2)));
            Assert.Equal("Channel created", notifications.LastTitle);
            Assert.Equal("News created successfully.", notifications.LastBody);
        }

        [Fact]
        public void Channel_confirm_dismisses_busy_on_urgent_dispatcher()
        {
            var dispatcher = new CountingDispatcher();
            var channels = new FakeChannels
            {
                NextResult = new ChannelCreateResult { ChannelJid = "120363000000000011@newsletter" }
            };
            var vm = CreateVm(
                groups: new FakeGroups(),
                chats: new RecordingChats(),
                channels: channels,
                dispatcher: dispatcher);
            string opened = null;
            vm.ChatOpenRequested += (s, jid) => opened = jid;
            vm.Open();
            vm.ReportLayoutMinimal(false);
            vm.NewChannelCommand.Execute(null);
            vm.ChannelName = "News";
            vm.ChannelPhotoBytes = new byte[] { 3 };

            vm.CreateNextCommand.Execute(null);

            Assert.True(SpinWait.SpinUntil(() => opened != null && !vm.IsCreating, TimeSpan.FromSeconds(2)));
            Assert.Equal(new byte[] { 3 }, channels.LastRequest.PhotoBytes);
            Assert.True(dispatcher.UrgentPasses > 0);
            Assert.False(vm.IsOpen);
        }

        [Fact]
        public async Task Channel_confirm_refused_shows_why_and_stays_on_the_form()
        {
            var dialogs = new FakeDialogs();
            var channels = new FakeChannels
            {
                NextResult = ChannelCreateResult.Failed(ChannelCreateResult.ReasonEmptyName)
            };
            var chats = new RecordingChats();
            var vm = CreateVm(dialogs, chats: chats, channels: channels);
            vm.ReportLayoutMinimal(false);
            vm.NewChannelCommand.Execute(null);

            vm.CreateNextCommand.Execute(null);
            await dialogs.WaitShownAsync();

            Assert.Equal("Enter a channel name.", dialogs.LastContent);
            Assert.Equal(NewConversationCreateFlow.Channel, vm.CreateFlow);
            Assert.Null(chats.LastStartJid);
        }

        [Fact]
        public async Task Channel_photo_failure_is_reported_after_the_channel_opens()
        {
            var dialogs = new FakeDialogs();
            var channels = new FakeChannels
            {
                NextResult = new ChannelCreateResult
                {
                    ChannelJid = "120363000000000010@newsletter",
                    PhotoFailed = true
                }
            };
            var vm = CreateVm(dialogs, chats: new RecordingChats(), channels: channels);
            vm.ReportLayoutMinimal(false);
            vm.NewChannelCommand.Execute(null);
            vm.ChannelName = "News";

            vm.CreateNextCommand.Execute(null);
            await dialogs.WaitShownAsync();

            Assert.Equal(
                "Channel created, but the photo couldn't be applied. You can change it in the channel info.",
                dialogs.LastContent);
        }

        [Fact]
        public void Leaving_channel_flow_clears_fields()
        {
            var vm = CreateVm();
            vm.ReportLayoutMinimal(true);
            vm.NewChannelCommand.Execute(null);
            vm.StartChannelCreateCommand.Execute(null);
            vm.ChannelName = "News";
            vm.ChannelPhotoBytes = new byte[] { 1 };

            vm.TryHandleCreateBack();

            Assert.Equal(NewConversationCreateFlow.None, vm.CreateFlow);
            Assert.False(vm.HasStartedChannelCreate);
            Assert.False(vm.HasChannelPhoto);
            Assert.Equal(string.Empty, vm.ChannelName);
        }

        [Fact]
        public void Leaving_community_flow_resets_started_flag()
        {
            var vm = CreateVm();
            vm.ReportLayoutMinimal(true);
            vm.NewCommunityCommand.Execute(null);
            vm.StartCommunityCreateCommand.Execute(null);

            vm.TryHandleCreateBack();

            Assert.Equal(NewConversationCreateFlow.None, vm.CreateFlow);
            Assert.False(vm.HasStartedCommunityCreate);
        }

        [Fact]
        public async Task PickCommunityPhoto_loads_bytes_from_file_picker()
        {
            var picker = new FakeFilePicker
            {
                NextImage = new PickedChatMedia
                {
                    Bytes = new byte[] { 1, 2, 3, 4 },
                    MimeType = "image/jpeg",
                    FileName = "photo.jpg",
                    IsImage = true
                }
            };
            var vm = CreateVm(filePicker: picker);
            vm.NewCommunityCommand.Execute(null);

            vm.PickCommunityPhotoCommand.Execute(null);
            await picker.WaitPickedAsync();
            Assert.True(SpinWait.SpinUntil(() => vm.HasCommunityPhoto, TimeSpan.FromSeconds(2)));

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, vm.CommunityPhotoBytes);
            Assert.Equal(1, picker.PickImageCount);
        }

        [Fact]
        public void Leaving_community_flow_clears_photo_bytes()
        {
            var picker = new FakeFilePicker
            {
                NextImage = new PickedChatMedia
                {
                    Bytes = new byte[] { 9 },
                    IsImage = true
                }
            };
            var vm = CreateVm(filePicker: picker);
            vm.NewCommunityCommand.Execute(null);
            vm.CommunityPhotoBytes = new byte[] { 9 };
            Assert.True(vm.HasCommunityPhoto);

            vm.TryHandleCreateBack();

            Assert.False(vm.HasCommunityPhoto);
            Assert.Null(vm.CommunityPhotoBytes);
        }

        [Fact]
        public void Group_info_ephemeral_defaults_to_off()
        {
            var vm = CreateVm();
            vm.NewGroupCommand.Execute(null);
            vm.CreateNextCommand.Execute(null);

            Assert.Equal(GroupEphemeralDuration.Off, vm.GroupEphemeralDuration);
            Assert.False(vm.HasGroupPhoto);
            Assert.False(vm.IsGroupPermissionsOpen);
        }

        [Fact]
        public async Task PickGroupPhoto_loads_bytes()
        {
            var picker = new FakeFilePicker
            {
                NextImage = new PickedChatMedia
                {
                    Bytes = new byte[] { 5, 6, 7 },
                    IsImage = true
                }
            };
            var vm = CreateVm(filePicker: picker);
            vm.NewGroupCommand.Execute(null);

            vm.PickGroupPhotoCommand.Execute(null);
            await picker.WaitPickedAsync();
            Assert.True(SpinWait.SpinUntil(() => vm.HasGroupPhoto, TimeSpan.FromSeconds(2)));
            Assert.Equal(new byte[] { 5, 6, 7 }, vm.GroupPhotoBytes);
        }

        [Fact]
        public async Task EditGroupEphemeral_applies_dialog_choice()
        {
            var dialogs = new FakeDialogs { NextEphemeral = GroupEphemeralDuration.Days7 };
            var vm = CreateVm(dialogs);
            vm.NewGroupCommand.Execute(null);

            vm.EditGroupEphemeralCommand.Execute(null);
            await dialogs.WaitEphemeralShownAsync();
            Assert.True(SpinWait.SpinUntil(
                () => vm.GroupEphemeralDuration == GroupEphemeralDuration.Days7,
                TimeSpan.FromSeconds(2)));
        }

        [Fact]
        public void SelectedMembersCount_tracks_selection_and_manage_returns_to_members_step()
        {
            var vm = CreateVm();
            vm.NewGroupCommand.Execute(null);
            Assert.Equal(0, vm.SelectedMembersCount);

            vm.SelectedMembers.Add(new ChatItem { JID = "a@s.whatsapp.net", Name = "A" });
            vm.SelectedMembers.Add(new ChatItem { JID = "b@s.whatsapp.net", Name = "B" });
            Assert.Equal(2, vm.SelectedMembersCount);

            vm.CreateNextCommand.Execute(null);
            Assert.Equal(1, vm.GroupStepIndex);

            vm.ManageGroupMembersCommand.Execute(null);
            Assert.Equal(0, vm.GroupStepIndex);
            Assert.Equal(NewConversationCreateFlow.Group, vm.CreateFlow);
            Assert.Equal(2, vm.SelectedMembersCount);
        }

        [Fact]
        public void Group_permissions_overlay_back_keeps_info_step()
        {
            var vm = CreateVm();
            vm.NewGroupCommand.Execute(null);
            vm.CreateNextCommand.Execute(null);

            vm.OpenGroupPermissionsCommand.Execute(null);
            Assert.True(vm.IsGroupPermissionsOpen);
            Assert.False(vm.ShowCreateFlowHeader);
            Assert.False(vm.ShowCreateActionChrome);

            Assert.True(vm.TryHandleCreateBack());
            Assert.False(vm.IsGroupPermissionsOpen);
            Assert.True(vm.ShowCreateFlowHeader);
            Assert.True(vm.ShowCreateActionChrome);
            Assert.Equal(NewConversationCreateFlow.Group, vm.CreateFlow);
            Assert.Equal(1, vm.GroupStepIndex);
        }

        [Fact]
        public void Closing_overlay_clears_create_flow()
        {
            var dispatcher = new CountingDispatcher();
            var chats = new ChatStateStore(dispatcher);
            var vm = new NewConversationViewModel(
                chats,
                new StubContacts(),
                chats: null,
                profile: null,
                jids: new SelfJids(),
                dialogs: new FakeDialogs(),
                newChatFactory: new FakeNewChatFactory(),
                strings: null,
                dispatcher: dispatcher);

            vm.Open();
            vm.NewGroupCommand.Execute(null);

            vm.Close();

            Assert.Equal(NewConversationCreateFlow.None, vm.CreateFlow);
            Assert.False(vm.IsOpen);
        }

        [Fact]
        public async Task NewContact_opens_people_editor()
        {
            var local = new FakeLocalContacts();
            var vm = CreateVm(localContacts: local);

            vm.NewContactCommand.Execute(null);
            await local.WaitOpenedAsync().ConfigureAwait(true);

            Assert.Equal(1, local.OpenNewContactCount);
        }

        private static NewConversationViewModel CreateVm(
            FakeDialogs dialogs = null,
            ILocalContactsService localContacts = null,
            IFilePicker filePicker = null,
            IGroupService groups = null,
            CountingDispatcher dispatcher = null,
            IChatService chats = null,
            INotificationService notifications = null,
            IChannelService channels = null)
        {
            dispatcher = dispatcher ?? new CountingDispatcher();
            return new NewConversationViewModel(
                new ChatStateStore(dispatcher),
                new StubContacts(),
                chats: chats,
                profile: null,
                jids: new SelfJids(),
                dialogs: dialogs ?? new FakeDialogs(),
                newChatFactory: new FakeNewChatFactory(),
                strings: null,
                dispatcher: dispatcher,
                localContacts: localContacts,
                filePicker: filePicker,
                groups: groups,
                notifications: notifications,
                channels: channels);
        }

        private sealed class FakeChannels : IChannelService
        {
            public ChannelCreateResult NextResult { get; set; }

            public ChannelCreateRequest LastRequest { get; private set; }

            public Task<ChannelCreateResult> CreateChannelAsync(ChannelCreateRequest request)
            {
                LastRequest = request;
                return Task.FromResult(NextResult);
            }

            public Task RefreshViewerRoleAsync(ChatItem channel) => Task.CompletedTask;
        }

        private sealed class RecordingNotifications : INotificationService
        {
            public string LastTitle { get; private set; }

            public string LastBody { get; private set; }

            public void ShowToast(string title, string body)
            {
                LastTitle = title;
                LastBody = body;
            }

            public void Initialize() { }

            public void NotifyIncomingMessage(
                string chatJid,
                string chatName,
                string senderName,
                string preview,
                bool isGroup,
                bool isMuted,
                bool suppressToast,
                int totalUnread,
                string avatarUrl = null,
                int chatUnread = -1,
                bool isCommunityAnnounce = false,
                bool isFavorite = false,
                bool isChannel = false)
            {
            }

            public void NotifyCallAlert(
                string title,
                string body,
                string chatJid = null,
                bool updateLiveTile = false,
                bool isFavorite = false)
            {
            }

            public void RefreshUnreadState(System.Collections.Generic.IEnumerable<ChatItem> chats) { }

            public void UpdateBadge(int totalUnread) { }

            public void ClearAll() { }

            public void OnNotificationsConfigChanged() { }
        }

        private sealed class FakeGroups : IGroupService
        {
            public GroupCreateResult NextResult { get; set; }

            /// <summary>When set, create waits on this before answering (busy-state tests).</summary>
            public Task Gate { get; set; }

            public GroupCreateRequest LastGroup { get; private set; }

            public CommunityCreateRequest LastCommunity { get; private set; }

            public int ForcedListings { get; private set; }

            public async Task<GroupCreateResult> CreateGroupAsync(GroupCreateRequest request)
            {
                LastGroup = request;
                if (Gate != null)
                {
                    await Gate.ConfigureAwait(false);
                }

                return NextResult;
            }

            public async Task<GroupCreateResult> CreateCommunityAsync(CommunityCreateRequest request)
            {
                LastCommunity = request;
                if (Gate != null)
                {
                    await Gate.ConfigureAwait(false);
                }

                return NextResult;
            }

            public Task QueryAllGroupsAsync(bool force)
            {
                if (force)
                {
                    ForcedListings++;
                }

                return Task.CompletedTask;
            }

            public Task QueryAllGroupsAsync() => Task.CompletedTask;

            public Task RefreshGroupSendPermissionsAsync(
                string groupJid, bool includeRoster = false, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task NoteSelfGroupMembershipAsync(
                string groupJid, IReadOnlyList<string> participantJids, bool stillMember) =>
                Task.CompletedTask;

            public Task EnsureGroupRosterLoadedFromStoreAsync(string groupJid) => Task.CompletedTask;

            public Task RestoreMyGroupRoleAsync(ChatItem group) => Task.CompletedTask;

            public GroupMember TryGetMember(string groupJid, string memberJid) => null;

            public void NoteMember(string groupJid, GroupMemberHint hint) { }

            public void RemoveMember(string groupJid, string memberJid, DateTime atUtc) { }

            public void ApplyRosterEvent(GroupRosterEvent change) { }

            public Task EnsureMembersAsync(string groupJid, IReadOnlyList<string> memberJids) =>
                Task.CompletedTask;

            public Task EnsureFullRosterAsync(
                string groupJid, string reason, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task EnsureHighQualityGroupAvatarAsync(ChatItem chat) => Task.CompletedTask;

            public Task<GroupMemberAvatarFetchResult> FetchGroupMemberAvatarAsync(
                GroupMember member, CancellationToken token) =>
                Task.FromResult<GroupMemberAvatarFetchResult>(null);

            public void ApplyGroupMemberAvatarOutcome(
                string memberJid,
                GroupMemberAvatarFetchResult result,
                string limitToGroupJid = null) { }

            public bool MemberMatches(GroupMember member, string candidateJid) => false;

            public IReadOnlyList<string> MembershipPersonKeys(GroupMember member) => Array.Empty<string>();

            public void EnrichMember(
                GroupMember member,
                string participantJid,
                ChatItem groupChat,
                ParticipantResolutionContext context,
                string nameHint = null) { }

            public string ResolveMemberAvatar(
                string participantJid,
                ChatItem groupChat,
                ParticipantResolutionContext context,
                GroupMember rosterMember = null) => null;

            public string ResolveMemberDisplayName(
                string participantJid,
                ChatItem groupChat,
                ParticipantResolutionContext context,
                string nameHint = null,
                GroupMember rosterMember = null) => null;

            public void InvalidateParticipantDirectChatIndex() { }

            public IGroupParticipantLookup CreateOpenChatParticipantLookup(Func<string> selfDisplayLabel) => null;

            public void EnrichMember(
                GroupMember member, string participantJid, ChatItem groupChat, string nameHint = null) { }

            public Task ApplyGroupMetadataAsync(string groupJid, GroupMetadataSnapshot metadata) =>
                Task.CompletedTask;

            public Task ApplyGroupMetadataFromResponseAsync(string groupJid, Unison.Baileys.Protocol.BinaryNode response) =>
                Task.CompletedTask;

            public void NotifyGroupListingDue() { }

            public Task ReconcileGroupCommunityTitlesAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task LeaveGroupAsync(string groupJid) => Task.CompletedTask;

            public Task<string> TryGetCreatedByYouCaptionAsync(
                string groupJid, string dateTemplate, CancellationToken cancellationToken = default) =>
                Task.FromResult<string>(null);

            public Task RefreshCommunityJoinableGroupsAsync(
                ChatItem communityHub, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<bool> JoinGroupAsync(
                string groupJid, string inviteCode = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);
        }

        private sealed class FakeFilePicker : IFilePicker
        {
            private readonly TaskCompletionSource<bool> _picked =
                new TaskCompletionSource<bool>();

            public PickedChatMedia NextImage { get; set; }

            public int PickImageCount { get; private set; }

            public Task WaitPickedAsync() => _picked.Task;

            public Task<string> PickOpenImagePathAsync() => Task.FromResult<string>(null);

            public Task<string> PickSaveTextFileAsync(string suggestedFileName, string content) =>
                Task.FromResult<string>(null);

            public Task<PickedChatMedia> PickChatAttachmentAsync() => Task.FromResult<PickedChatMedia>(null);

            public Task<PickedChatMedia> PickChatImageAsync()
            {
                PickImageCount++;
                _picked.TrySetResult(true);
                return Task.FromResult(NextImage);
            }

            public Task<IReadOnlyList<PickedChatMedia>> PickChatImagesAsync() =>
                Task.FromResult<IReadOnlyList<PickedChatMedia>>(null);

            public Task<PickedChatMedia> PickChatAudioAsync() => Task.FromResult<PickedChatMedia>(null);

            public Task<PickedChatMedia> PickChatDocumentAsync() => Task.FromResult<PickedChatMedia>(null);

            public Task<string> PickSaveLocalImageAsync(string sourceUriOrPath, string suggestedFileName) =>
                Task.FromResult<string>(null);

            public Task<string> PickSaveLocalFileAsync(
                string sourceUriOrPath,
                string suggestedFileName,
                string mimeType = null) =>
                Task.FromResult<string>(null);
        }

        private sealed class FakeLocalContacts : ILocalContactsService
        {
            private readonly TaskCompletionSource<bool> _opened =
                new TaskCompletionSource<bool>();

            public int OpenNewContactCount { get; private set; }

            public Task WaitOpenedAsync() => _opened.Task;

            public Task<Dictionary<string, string>> LoadPhoneContactNamesAsync() =>
                Task.FromResult(new Dictionary<string, string>());

            public bool IsPhoneInAddressBook(string phoneDigits) => false;

            public Task<bool> ShowSystemContactCardAsync(
                string displayName, string phoneDigits, string photoUri) =>
                Task.FromResult(false);

            public Task<bool> OpenNewContactEditorAsync()
            {
                OpenNewContactCount++;
                _opened.TrySetResult(true);
                return Task.FromResult(true);
            }

            public Task PublishAppContactsAsync(IReadOnlyList<AppContactExport> contacts) =>
                Task.CompletedTask;

            public Task ClearPublishedAppContactsAsync() => Task.CompletedTask;
        }

        private sealed class FakeDialogs : IDialogService
        {
            private readonly TaskCompletionSource<bool> _shown =
                new TaskCompletionSource<bool>();

            public bool MessageShown { get; private set; }

            public string LastContent { get; private set; }

            public Task WaitShownAsync() => _shown.Task;

            public Func<bool> IsOnUi { get; set; }

            public bool ShownOnUi { get; private set; }

            public Task ShowMessageAsync(string title, string content, string closeButtonText)
            {
                ShownOnUi = IsOnUi != null && IsOnUi();
                MessageShown = true;
                LastContent = content;
                _shown.TrySetResult(true);
                return Task.CompletedTask;
            }

            public Task<bool> ShowConfirmAsync(string title, string content, string primaryButtonText, string closeButtonText) =>
                Task.FromResult(false);

            public Task<MessageDeleteChoice> ShowMessageDeleteAsync(
                string content, bool offerDeleteForEveryone, string deleteForMeText, string deleteForEveryoneText, string cancelText) =>
                Task.FromResult(MessageDeleteChoice.Cancel);

            public Task<ConfirmPromptResult> ShowConfirmResultAsync(
                string title, string content, string primaryButtonText, string closeButtonText) =>
                Task.FromResult(ConfirmPromptResult.Cancel);

            public Task<string> ShowInputAsync(
                string title, string prompt, string placeholder, string primaryButtonText, string closeButtonText) =>
                Task.FromResult<string>(null);

            public Task ShowPairingCodeAsync(LoginViewModel loginVm, string code) => Task.CompletedTask;

            public Task<string> ShowNewChatDialogAsync(NewChatDialogViewModel newChatVm) =>
                Task.FromResult<string>(null);

            public Task<bool> ShowGroupPreviewAsync(GroupPreviewDialogViewModel previewVm) =>
                Task.FromResult(false);

            public Task<ImageSendPreviewResult> ShowImageSendPreviewAsync(
                IReadOnlyList<byte[]> imageBytes, string infoText, string initialCaption = null) =>
                Task.FromResult(new ImageSendPreviewResult());

            public Task<ImageSendPreviewResult> ShowDocumentSendPreviewAsync(
                string fileName, string infoText, string initialCaption = null) =>
                Task.FromResult(new ImageSendPreviewResult());

            public Task ShowQrFullscreenAsync(LoginViewModel loginVm) => Task.CompletedTask;

            public Task ShowQrFullscreenAsync(string qrData) => Task.CompletedTask;

            public Task ShowWhatsAppSettingsAsync(SettingsViewModel settingsVm, bool openProfile = false) =>
                Task.CompletedTask;

            public Task ShowReactionsDialogAsync(ChatMessageViewModel messageVm) => Task.CompletedTask;

            public Task ShowRuntimeDiagnosticsAsync() => Task.CompletedTask;

            public Task<StartTileSelection> ShowStartTileMarkAsync(StartTileMark current, bool transparent) =>
                Task.FromResult<StartTileSelection>(null);

            public Task<LockScreenNotificationScope?> ShowNotificationFilterAsync(LockScreenNotificationScope current) =>
                Task.FromResult<LockScreenNotificationScope?>(null);

            public Task<bool> ShowChatExportOptionsAsync() => Task.FromResult(false);

            public Task<ChatExportReadyChoice> ShowChatExportReadyAsync(string fileName, string infoText) =>
                Task.FromResult(ChatExportReadyChoice.Dismissed);

            public Task<StatusPrivacyDialogChoice> ShowStatusPrivacyAsync(StatusComposerViewModel composer) =>
                Task.FromResult(StatusPrivacyDialogChoice.Cancel);

            public Task ShowCallBackAsync(string phoneDigitsOrRaw) => Task.CompletedTask;

            public GroupEphemeralDuration? NextEphemeral { get; set; }

            private readonly TaskCompletionSource<bool> _ephemeralShown =
                new TaskCompletionSource<bool>();

            public Task WaitEphemeralShownAsync() => _ephemeralShown.Task;

            public Task<GroupEphemeralDuration?> ShowGroupEphemeralAsync(GroupEphemeralDuration current)
            {
                _ephemeralShown.TrySetResult(true);
                return Task.FromResult(NextEphemeral);
            }
        }

        private sealed class StubContacts : IContactService
        {
            public event EventHandler DisplayNamesUpdated { add { } remove { } }

            public void NotifyDisplayNamesUpdated() { }

            public void NotifyAvatarCached(string jid, string localAvatarUrl) { }

            public void NotifyJidAliasResolved(string phoneJid, string lidJid, string source) { }

            public void FlushDeferredAliasFollowUp(string source) { }

            public string ResolveDisplayName(string jid, string context) => jid;

            public bool IsSelf(string jid) => false;

            public string ResolvePlaceholderChatName(string jid) => jid;

            public IReadOnlyDictionary<string, string> PhoneContactNamesByJid { get; } =
                new Dictionary<string, string>();

            public void MarkAvatarImageLoadFailed(ChatItem chat, string reason) { }

            public Task<Dictionary<string, string>> SyncLocalContactsAsync(
                IEnumerable<string> directChatJids, CancellationToken cancellationToken = default) =>
                Task.FromResult(new Dictionary<string, string>());

            public Task RetrieveContactPicturesAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task HydrateCachedAvatarUrisAsync(string reason) => Task.CompletedTask;

            public Task HydrateGroupMemberAvatarsAsync(string groupJid) => Task.CompletedTask;

            public Task HydrateGroupMemberAvatarsForJidsAsync(string groupJid, IReadOnlyList<string> memberJids) =>
                Task.CompletedTask;

            public bool IsHydratingGroupMemberAvatars => false;

            public Task NotifyAvatarCachedAsync(string jid, string localAvatarUrl) => Task.CompletedTask;

            public Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false) =>
                Task.CompletedTask;

            public Task RefreshPhoneContactOverlayAsync(bool force) => Task.CompletedTask;

            public Task RefreshChatListAddressBookNamesAsync() => Task.CompletedTask;

            public bool IsContactRefreshRunning => false;

            public bool IsContactRefreshOnCooldown => false;

            public Task ResolveMissingNamesAsync(bool force = false, bool includeGroupListing = true) =>
                Task.CompletedTask;

            public Task RunBackgroundResolutionAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task NotifyBackgroundResolutionDueAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public void TriggerBackgroundResolution() { }

            public void TriggerDeferredAvatarRetry() { }

            public void CancelBackgroundResolution() { }

            public Task RunDeferredStartupContactPassAsync(bool resolveMissingNames = false) =>
                Task.CompletedTask;

            public void RequestAvatarRefresh(ChatItem chat, bool force = false) { }

            public void CancelAliasFollowUp() { }

            public Task ApplyRemoteGroupSubjectsAsync(IEnumerable<KeyValuePair<string, string>> subjectsByJid) =>
                Task.CompletedTask;

            public Task ApplyRemoteContactNameAsync(string jid, string name) => Task.CompletedTask;

            public Task ApplyRemoteSelfPushNameAsync(string name) => Task.CompletedTask;

            public void CaptureSelfPushName(string pushName, string source = null) { }

            public void RememberPersonName(string jid, string displayName) { }

            public void AcceptIncomingPushName(
                string rawName, bool isFromMe, string participantJid, string fromJid, bool isOffline) { }

            public void ApplyStanzaNotifyPushName(string fromJid, string notify, string participantJid) { }

            public void NotifyInboundMessageNode(string fromJid, string notify, string participantJid) { }

            public void RegisterIncomingEnvelopeAliasHints(
                string senderLid, string fromJid, string peerRecipientPn, string peerRecipientLid,
                string recipientJid, string participant, string participantAlt) { }

            public Task ApplyRemoteContactUpdatesAsync(IReadOnlyList<AccountContactUpdate> contacts) =>
                Task.CompletedTask;

            public Task NormalizePersistedChatNamesAsync() => Task.CompletedTask;

            public Task ApplyResolvedNamesToChatListAsync() => Task.CompletedTask;

            public Task ResolveGroupJoinParticipantsAsync(
                IEnumerable<KeyValuePair<string, string>> participantJidToPhone) =>
                Task.CompletedTask;

            public Task<string> SearchContactAsync(string phoneNumber) => Task.FromResult<string>(null);

            public string TryResolvePhone(string jid, string phoneDigits = null) => null;

            public bool CanAddToAddressBook(string jid, string phoneDigits = null) => false;

            public Task<bool> ShowAddToAddressBookAsync(
                string displayName, string phoneDigits, string photoUri = null, string jid = null) =>
                Task.FromResult(false);

            public Task SetPublishContactsToWindowsAsync(bool enabled) => Task.CompletedTask;

            public Task BlockContactAsync(string jid) => Task.CompletedTask;
        }

        private sealed class SelfJids : IJidResolver
        {
            public Profile Self => null;

            public string GetCanonicalJid(string jid) => JidHelper.Normalize(jid) ?? jid;
            public string PeekCanonicalJid(string jid) => GetCanonicalJid(jid);

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }

        private sealed class FakeNewChatFactory : INewChatDialogViewModelFactory
        {
            public NewChatDialogViewModel Create() => null;
        }
    }
}
