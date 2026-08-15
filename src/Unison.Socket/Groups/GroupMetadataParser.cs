// =============================================================================
// GroupMetadataParser
//
// Turns a <group> node into GroupMetadata.
//
// It is deliberately the only place that knows those attribute names. Group
// nodes arrive from three different queries - single metadata, the participating
// list, invite info - and today each caller in WhatsAppService picks out the two
// or three attributes it happens to need, so the three disagree about the same
// group. One parser, three callers.
//
// Ports: rc14 extractGroupMetadata in src/Socket/groups.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.Models;
using Unison.Socket.WABinary;

namespace Unison.Socket.Groups
{
    public static class GroupMetadataParser
    {
        /// <summary>
        /// Reads the group carried by <paramref name="result"/>, or null when the reply holds no
        /// group node. Callers check for an error child themselves: a refusal is a normal answer
        /// here, since not being in a group is a state and not a fault.
        /// </summary>
        public static GroupMetadata Parse(BinaryNode result)
        {
            if (result == null)
            {
                return null;
            }

            var group = result.Tag == "group" ? result : result.GetChild("group");
            if (group == null || string.IsNullOrEmpty(group.GetAttribute("id")))
            {
                return null;
            }

            var id = group.GetAttribute("id");
            var metadata = new GroupMetadata
            {
                Id = id.Contains("@") ? id : WA.JidEncode(id, WA.G_US),
                Notify = group.GetAttribute("notify"),
                AddressingMode = ReadAddressingMode(group.GetAttribute("addressing_mode")),
                Subject = group.GetAttribute("subject"),
                SubjectOwner = group.GetAttribute("s_o"),
                SubjectOwnerPn = group.GetAttribute("s_o_pn"),
                SubjectOwnerUsername = group.GetAttribute("s_o_username"),
                SubjectTime = ReadLong(group.GetAttribute("s_t")),
                Creation = ReadLong(group.GetAttribute("creation")),
                Owner = NormalizeOrNull(group.GetAttribute("creator")),
                OwnerPn = NormalizeOrNull(group.GetAttribute("creator_pn")),
                OwnerUsername = group.GetAttribute("creator_username"),
                OwnerCountryCode = group.GetAttribute("creator_country_code"),
                Restrict = group.GetChild("locked") != null,
                Announce = group.GetChild("announcement") != null,
                IsCommunity = group.GetChild("parent") != null,
                IsCommunityAnnounce = group.GetChild("default_sub_group") != null,
                JoinApprovalMode = group.GetChild("membership_approval_mode") != null,
                MemberAddMode = ReadChildText(group, "member_add_mode") == "all_member_add"
            };

            var description = group.GetChild("description");
            if (description != null)
            {
                metadata.Description = ReadChildText(description, "body");
                metadata.DescriptionId = description.GetAttribute("id");
                metadata.DescriptionOwner = NormalizeOrNull(description.GetAttribute("participant"));
                metadata.DescriptionOwnerPn = NormalizeOrNull(description.GetAttribute("participant_pn"));
                metadata.DescriptionOwnerUsername = description.GetAttribute("participant_username");
                metadata.DescriptionTime = ReadLong(description.GetAttribute("t"));
            }

            var linkedParent = group.GetChild("linked_parent");
            if (linkedParent != null)
            {
                metadata.LinkedParent = linkedParent.GetAttribute("jid");
            }

            var ephemeral = group.GetChild("ephemeral");
            if (ephemeral != null)
            {
                var expiration = ReadLong(ephemeral.GetAttribute("expiration"));
                if (expiration > 0)
                {
                    metadata.EphemeralDuration = (int)expiration;
                }
            }

            foreach (var participant in group.GetChildren("participant"))
            {
                var participantId = participant.GetAttribute("jid");
                if (string.IsNullOrEmpty(participantId))
                {
                    continue;
                }

                var phoneNumber = participant.GetAttribute("phone_number");
                var lid = participant.GetAttribute("lid");

                metadata.Participants.Add(new GroupParticipant
                {
                    Id = participantId,

                    // Only trust the counterpart attributes when they really are the other space:
                    // the server sometimes echoes the same value back in both.
                    PhoneNumber = JidUtils.IsAnyLid(participantId) && JidUtils.IsAnyPn(phoneNumber)
                        ? phoneNumber
                        : null,
                    Lid = JidUtils.IsAnyPn(participantId) && JidUtils.IsAnyLid(lid) ? lid : null,
                    Username = participant.GetAttribute("participant_username") ?? participant.GetAttribute("username"),
                    Role = ReadRole(participant.GetAttribute("type"))
                });
            }

            var size = ReadLong(group.GetAttribute("size"));
            metadata.Size = size > 0 ? (int)size : metadata.Participants.Count;

            return metadata;
        }

        /// <summary>
        /// Collects the LID/PN pairs the group disclosed, so a metadata fetch also teaches the
        /// mapping store. Baileys leaves this as a TODO; the pairs are free and Unison needs them
        /// to stop showing the same member twice.
        /// </summary>
        public static IReadOnlyList<Signal.LidMapping> ExtractLidMappings(GroupMetadata metadata)
        {
            var mappings = new List<Signal.LidMapping>();
            if (metadata == null)
            {
                return mappings;
            }

            foreach (var participant in metadata.Participants)
            {
                if (JidUtils.IsAnyLid(participant.Id) && !string.IsNullOrEmpty(participant.PhoneNumber))
                {
                    mappings.Add(new Signal.LidMapping(participant.Id, participant.PhoneNumber));
                }
                else if (JidUtils.IsAnyPn(participant.Id) && !string.IsNullOrEmpty(participant.Lid))
                {
                    mappings.Add(new Signal.LidMapping(participant.Lid, participant.Id));
                }
            }

            return mappings;
        }

        private static GroupParticipantRole ReadRole(string type)
        {
            if (type == "superadmin")
            {
                return GroupParticipantRole.SuperAdmin;
            }

            return type == "admin" ? GroupParticipantRole.Admin : GroupParticipantRole.Member;
        }

        private static string ReadChildText(BinaryNode node, string tag)
        {
            var child = node.GetChild(tag);
            return child != null ? child.GetContentString() : null;
        }

        private static string NormalizeOrNull(string jid)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return null;
            }

            var normalized = JidUtils.NormalizedUser(jid);
            return string.IsNullOrEmpty(normalized) ? null : normalized;
        }

        private static long ReadLong(string value)
        {
            long parsed;
            return long.TryParse(value, out parsed) ? parsed : 0;
        }

        /// <summary>
        /// An absent attribute stays absent rather than becoming pn, because the send path has
        /// its own answer for a group that never said which space it uses.
        /// </summary>
        private static GroupAddressingMode ReadAddressingMode(string value)
        {
            if (value == "lid")
            {
                return GroupAddressingMode.Lid;
            }

            return value == "pn" ? GroupAddressingMode.Pn : GroupAddressingMode.Unset;
        }
    }
}
