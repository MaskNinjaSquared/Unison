// =============================================================================
// GroupCreation
//
// Creates a group or community from the New Conversation forms and applies the
// follow-ups the form asked for. GroupFacade delegates here.
//
// Order matters: picture and disappearing timer address the new JID, so they
// run only after create returns. Naming the subject hits the UI list rename
// path (ApplyResolvedNameBatchAsync) and must not gate those wire follow-ups —
// 85eeb434 stalled ~100s on the rename and never reached picture-set.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Groups
{
    public sealed class GroupCreation
    {
        private const string ReasonEmptySubject = GroupCreateResult.ReasonEmptySubject;
        private const string ReasonNotConnected = GroupCreateResult.ReasonNotConnected;
        private const string ReasonNoGroup = GroupCreateResult.ReasonNoGroup;

        private readonly IGroupWireActions _wire;
        private readonly Func<string, string, Task> _nameGroup;
        private readonly IProfilePictureEncoder _pictures;

        /// <param name="nameGroup">Files (jid, subject) in the name caches the chat list reads.</param>
        public GroupCreation(
            IGroupWireActions wire,
            Func<string, string, Task> nameGroup,
            IProfilePictureEncoder pictures = null)
        {
            _wire = wire ?? throw new ArgumentNullException(nameof(wire));
            _nameGroup = nameGroup ?? throw new ArgumentNullException(nameof(nameGroup));
            _pictures = pictures;
        }

        public async Task<GroupCreateResult> CreateGroupAsync(GroupCreateRequest request)
        {
            string subject = request?.Subject?.Trim();
            if (string.IsNullOrEmpty(subject))
            {
                return GroupCreateResult.Failed(ReasonEmptySubject);
            }

            // WhatsApp lets the creator start a group alone and add people later.
            List<string> participants = DistinctJids(request.ParticipantJids);

            if (!_wire.IsHandshakeComplete)
            {
                return GroupCreateResult.Failed(ReasonNotConnected);
            }

            Debug.WriteLine("[GroupCreation] create group with " + participants.Count + " participant(s)");
            string jid;
            try
            {
                jid = await _wire.CreateGroupAsync(subject, participants).ConfigureAwait(false);
                Debug.WriteLine("[GroupCreation] created group " + (jid ?? "(none)"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupCreation] CreateGroupAsync: " + ex.Message);
                return GroupCreateResult.Failed(ex.Message);
            }

            if (string.IsNullOrWhiteSpace(jid))
            {
                return GroupCreateResult.Failed(ReasonNoGroup);
            }

            var result = new GroupCreateResult { GroupJid = jid };
            result.PhotoFailed = !await TrySetPictureAsync(jid, request.PhotoBytes).ConfigureAwait(false);
            result.EphemeralFailed = !await TrySetEphemeralAsync(jid, request.Ephemeral.ToSeconds())
                .ConfigureAwait(false);
            // StartNewChat already seeds the row subject; cache fill must not block the photo IQ.
            ScheduleName(jid, subject);
            return result;
        }

        public async Task<GroupCreateResult> CreateCommunityAsync(CommunityCreateRequest request)
        {
            string subject = request?.Subject?.Trim();
            if (string.IsNullOrEmpty(subject))
            {
                return GroupCreateResult.Failed(ReasonEmptySubject);
            }

            if (!_wire.IsHandshakeComplete)
            {
                return GroupCreateResult.Failed(ReasonNotConnected);
            }

            string jid;
            try
            {
                jid = await _wire
                    .CreateCommunityAsync(subject, request.Description?.Trim() ?? string.Empty)
                    .ConfigureAwait(false);
                Debug.WriteLine("[GroupCreation] created community " + (jid ?? "(none)"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupCreation] CreateCommunityAsync: " + ex.Message);
                return GroupCreateResult.Failed(ex.Message);
            }

            if (string.IsNullOrWhiteSpace(jid))
            {
                return GroupCreateResult.Failed(ReasonNoGroup);
            }

            var result = new GroupCreateResult { GroupJid = jid };
            result.PhotoFailed = !await TrySetPictureAsync(jid, request.PhotoBytes).ConfigureAwait(false);
            ScheduleName(jid, subject);
            return result;
        }

        /// <summary>
        /// The w:gp2 create notification names the group too, but only after a round trip; the
        /// chat opened right after the create would show its JID until then.
        /// </summary>
        private void ScheduleName(string jid, string subject)
        {
            _ = NameAsync(jid, subject);
        }

        private async Task NameAsync(string jid, string subject)
        {
            try
            {
                await _nameGroup(jid, subject).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupCreation] subject cache: " + ex.Message);
            }
        }

        /// <returns>True when there was nothing to set or the picture went up.</returns>
        private async Task<bool> TrySetPictureAsync(string jid, byte[] photo)
        {
            if (photo == null || photo.Length == 0)
            {
                Debug.WriteLine("[GroupCreation] picture skipped: no bytes");
                return true;
            }

            if (_pictures == null)
            {
                Debug.WriteLine("[GroupCreation] picture skipped: no encoder");
                return false;
            }

            try
            {
                byte[] jpeg = await _pictures.EncodeSquareJpegAsync(photo).ConfigureAwait(false);
                if (jpeg == null || jpeg.Length == 0)
                {
                    Debug.WriteLine("[GroupCreation] picture skipped: could not decode; rawBytes=" + photo.Length);
                    return false;
                }

                Debug.WriteLine(
                    "[GroupCreation] picture upload jid=" + jid +
                    "; rawBytes=" + photo.Length +
                    "; jpegBytes=" + jpeg.Length);
                await _wire.UpdateGroupPictureAsync(jid, jpeg).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupCreation] picture: " + ex.Message);
                return false;
            }
        }

        /// <returns>True when the timer is off or the server took it.</returns>
        private async Task<bool> TrySetEphemeralAsync(string jid, int seconds)
        {
            if (seconds <= 0)
            {
                return true;
            }

            try
            {
                await _wire.UpdateGroupEphemeralAsync(jid, seconds).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupCreation] ephemeral: " + ex.Message);
                return false;
            }
        }

        private static List<string> DistinctJids(IReadOnlyList<string> jids)
        {
            var list = new List<string>();
            if (jids == null)
            {
                return list;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < jids.Count; i++)
            {
                string jid = jids[i]?.Trim();
                if (!string.IsNullOrEmpty(jid) && seen.Add(jid))
                {
                    list.Add(jid);
                }
            }

            return list;
        }
    }
}
