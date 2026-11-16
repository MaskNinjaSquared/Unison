// =============================================================================
// CallStanzaBuilders
//
// Outbound <call> wrappers for reject / terminate.
// Ports: meowcaller signaling.BuildReject / BuildTerminate
// (https://github.com/purpshell/meowcaller/blob/main/signaling/stanza.go)
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Baileys.Protocol;

namespace Unison.Socket.UseCases.Calls
{
    public static class CallStanzaBuilders
    {
        /// <summary>
        /// <c>&lt;call to=toJid&gt;&lt;reject call-id call-creator/&gt;&lt;/call&gt;</c>
        /// </summary>
        public static BinaryNode BuildReject(string callId, string toJid, string callCreatorJid)
        {
            Require(callId, nameof(callId));
            Require(toJid, nameof(toJid));
            Require(callCreatorJid, nameof(callCreatorJid));

            var action = new BinaryNode(
                "reject",
                new Dictionary<string, string>
                {
                    { "call-id", callId },
                    { "call-creator", callCreatorJid }
                });

            return WrapCall(toJid, action);
        }

        /// <summary>
        /// <c>&lt;call to=toJid&gt;&lt;terminate call-id call-creator [reason]/gt;&lt;/call&gt;</c>
        /// </summary>
        public static BinaryNode BuildTerminate(
            string callId,
            string toJid,
            string callCreatorJid,
            string reason = null)
        {
            Require(callId, nameof(callId));
            Require(toJid, nameof(toJid));
            Require(callCreatorJid, nameof(callCreatorJid));

            var attrs = new Dictionary<string, string>
            {
                { "call-id", callId },
                { "call-creator", callCreatorJid }
            };
            if (!string.IsNullOrEmpty(reason))
            {
                attrs["reason"] = reason;
            }

            var action = new BinaryNode("terminate", attrs);
            return WrapCall(toJid, action);
        }

        private static BinaryNode WrapCall(string toJid, BinaryNode action)
        {
            return new BinaryNode(
                "call",
                new Dictionary<string, string> { { "to", toJid } },
                new List<BinaryNode> { action });
        }

        private static void Require(string value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(name + " is required", name);
            }
        }
    }
}
