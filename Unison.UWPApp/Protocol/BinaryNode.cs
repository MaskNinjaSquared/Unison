using System;
using System.Collections.Generic;

namespace Unison.UWPApp.Protocol
{
    /// <summary>
    /// Represents a node in WhatsApp's binary XML protocol
    /// </summary>
    public class BinaryNode
    {
        public string Tag { get; set; }
        public Dictionary<string, string> Attrs { get; set; }
        public object Content { get; set; } // Can be byte[], string, or List<BinaryNode>
        public List<BinaryNode> Children => GetAllChildren();

        public BinaryNode()
        {
            Attrs = new Dictionary<string, string>();
        }

        public BinaryNode(string tag) : this()
        {
            Tag = tag;
        }

        public BinaryNode(string tag, Dictionary<string, string> attrs) : this(tag)
        {
            if (attrs != null)
            {
                foreach (var kv in attrs)
                {
                    Attrs[kv.Key] = kv.Value;
                }
            }
        }

        public BinaryNode(string tag, Dictionary<string, string> attrs, object content) : this(tag, attrs)
        {
            Content = content;
        }

        /// <summary>
        /// Gets a child node by tag name
        /// </summary>
        public BinaryNode GetChild(string tag)
        {
            if (Content is BinaryNode node)
            {
                return node.Tag == tag ? node : null;
            }
            if (Content is List<BinaryNode> children)
            {
                foreach (var child in children)
                {
                    if (child.Tag == tag)
                        return child;
                }
            }
            return null;
        }

        /// <summary>
        /// Safely gets an attribute value
        /// </summary>
        public string GetAttribute(string key)
        {
            return Attrs.TryGetValue(key, out var val) ? val : null;
        }

        /// <summary>
        /// Gets all children with a specific tag
        /// </summary>
        public List<BinaryNode> GetChildren(string tag)
        {
            var result = new List<BinaryNode>();
            if (Content is BinaryNode node)
            {
                if (node.Tag == tag) result.Add(node);
            }
            else if (Content is List<BinaryNode> children)
            {
                foreach (var child in children)
                {
                    if (child.Tag == tag)
                        result.Add(child);
                }
            }
            return result;
        }

        /// <summary>
        /// Gets all children
        /// </summary>
        public List<BinaryNode> GetAllChildren()
        {
            if (Content is List<BinaryNode> children)
                return children;
            if (Content is BinaryNode node)
                return new List<BinaryNode> { node };
            return new List<BinaryNode>();
        }

        /// <summary>
        /// Recursively finds the first child with the given tag
        /// </summary>
        public BinaryNode FindDescendant(string tag)
        {
            if (Tag == tag) return this;
            if (Content is BinaryNode node)
            {
                return node.FindDescendant(tag);
            }
            if (Content is List<BinaryNode> children)
            {
                foreach (var child in children)
                {
                    var found = child.FindDescendant(tag);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Recursively finds all children with the given tag
        /// </summary>
        public List<BinaryNode> FindAllDescendants(string tag)
        {
            var results = new List<BinaryNode>();
            FindAllDescendantsRecursive(this, tag, results);
            return results;
        }

        private void FindAllDescendantsRecursive(BinaryNode node, string tag, List<BinaryNode> results)
        {
            if (node.Tag == tag) results.Add(node);
            if (node.Content is BinaryNode single)
            {
                FindAllDescendantsRecursive(single, tag, results);
            }
            else if (node.Content is List<BinaryNode> children)
            {
                foreach (var child in children)
                {
                    FindAllDescendantsRecursive(child, tag, results);
                }
            }
        }

        /// <summary>
        /// Gets content as byte array
        /// </summary>
        public byte[] GetContentBytes()
        {
            if (Content is byte[] bytes)
                return bytes;
            if (Content is string str)
                return System.Text.Encoding.UTF8.GetBytes(str);
            return null;
        }

        /// <summary>
        /// Gets content as string
        /// </summary>
        public string GetContentString()
        {
            if (Content is string str)
                return str;
            if (Content is byte[] bytes)
                return System.Text.Encoding.UTF8.GetString(bytes);
            return null;
        }

        public override string ToString()
        {
            return ToString(0);
        }

        private string ToString(int indent)
        {
            var sb = new System.Text.StringBuilder();
            var pad = new string(' ', indent * 2);
            
            sb.Append(pad);
            sb.Append("<");
            sb.Append(Tag);
            
            foreach (var attr in Attrs)
            {
                sb.Append($" {attr.Key}=\"{attr.Value}\"");
            }

            if (Content == null)
            {
                sb.Append(" />");
            }
            else if (Content is BinaryNode node)
            {
                sb.AppendLine(">");
                sb.AppendLine(node.ToString(indent + 1));
                sb.Append(pad);
                sb.Append($"</{Tag}>");
            }
            else if (Content is List<BinaryNode> children)
            {
                sb.AppendLine(">");
                foreach (var child in children)
                {
                    sb.AppendLine(child.ToString(indent + 1));
                }
                sb.Append(pad);
                sb.Append($"</{Tag}>");
            }
            else if (Content is byte[] bytes)
            {
                sb.Append($">[{bytes.Length} bytes]</{Tag}>");
            }
            else
            {
                sb.Append($">{Content}</{Tag}>");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Common WhatsApp S.WHATSAPP.NET JID
    /// </summary>
    public static class WA
    {
        public const string S_WHATSAPP_NET = "s.whatsapp.net";
        public const string G_US = "g.us";

        // Domain-type values used in AD_JID encoding (Baileys WAJIDDomains)
        private const int DomainTypeWhatsApp = 0;
        private const int DomainTypeLid = 1;
        private const int DomainTypeHosted = 128;
        private const int DomainTypeHostedLid = 129;
        
        public static string JidEncode(string user, string server = S_WHATSAPP_NET)
        {
            return $"{user}@{server}";
        }

        /// <summary>
        /// Encodes a JID with an optional device id (Baileys jidEncode).
        /// Device 0 is treated as "no device" and omitted in the string form.
        /// </summary>
        public static string JidEncode(string user, string server, int device)
        {
            if (user == null) user = string.Empty;

            string userPart = user;
            if (device > 0)
            {
                userPart = $"{userPart}:{device}";
            }

            return $"{userPart}@{server}";
        }

        /// <summary>
        /// Encodes a JID with an agent/domainType suffix and optional device id.
        /// </summary>
        public static string JidEncode(string user, string server, int device, int agent)
        {
            if (user == null) user = string.Empty;

            string userPart = user;
            if (agent != 0)
            {
                userPart = $"{userPart}_{agent}";
            }

            if (device > 0)
            {
                userPart = $"{userPart}:{device}";
            }

            return $"{userPart}@{server}";
        }

        /// <summary>
        /// Parses a JID into user/server, optional device id, and AD_JID domainType.
        /// Accepts both current Baileys-style user[_agent][:device]@server and legacy Unison dot forms.
        /// </summary>
        public static bool TryDecodeJid(string jid, out string user, out string server, out int? device, out int domainType)
        {
            user = null;
            server = null;
            device = null;
            domainType = DomainTypeWhatsApp;

            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            jid = jid.Trim();
            int atIndex = jid.IndexOf('@');
            if (atIndex <= 0 || atIndex >= jid.Length - 1)
            {
                return false;
            }

            string serverRaw = jid.Substring(atIndex + 1).Trim();
            string userCombined = jid.Substring(0, atIndex).Trim();

            // Split optional device
            string userAgent = userCombined;
            string deviceText = null;
            int colonIndex = userCombined.IndexOf(':');
            if (colonIndex > 0 && colonIndex < userCombined.Length - 1)
            {
                userAgent = userCombined.Substring(0, colonIndex);
                deviceText = userCombined.Substring(colonIndex + 1);

                // Legacy Unison form can append ".agent" after the device id
                int dotInDevice = deviceText.IndexOf('.');
                if (dotInDevice > 0)
                {
                    deviceText = deviceText.Substring(0, dotInDevice);
                }

                if (int.TryParse(deviceText, out int parsedDevice))
                {
                    device = parsedDevice;
                }
                else
                {
                    device = 0;
                }
            }

            // Split optional agent (Baileys uses "_" — accept legacy "." only when numeric)
            int agentValue = 0;
            string baseUser = userAgent;
            int underscoreIndex = userAgent.IndexOf('_');
            if (underscoreIndex > 0 && underscoreIndex < userAgent.Length - 1)
            {
                baseUser = userAgent.Substring(0, underscoreIndex);
                string agentText = userAgent.Substring(underscoreIndex + 1);
                int.TryParse(agentText, out agentValue);
            }
            else
            {
                // Legacy Unison AD-JID string format used "." for agent (e.g. user.agent:device@server).
                // Only treat "." as agent when a device suffix is present to avoid breaking real dotted ids.
                if (!string.IsNullOrEmpty(deviceText))
                {
                    int dotIndex = userAgent.IndexOf('.');
                    if (dotIndex > 0 && dotIndex < userAgent.Length - 1)
                    {
                        string agentText = userAgent.Substring(dotIndex + 1);
                        if (int.TryParse(agentText, out agentValue))
                        {
                            baseUser = userAgent.Substring(0, dotIndex);
                        }
                    }
                }
            }

            string normalizedServer = serverRaw.ToLowerInvariant();
            server = normalizedServer;

            // Legacy Unison sometimes persisted PN JIDs with a ".0" shard suffix (e.g. "4477....0@s.whatsapp.net").
            // Collapse only ".0" to avoid breaking meaningful dotted ids used elsewhere.
            if (normalizedServer == S_WHATSAPP_NET && baseUser.EndsWith(".0", StringComparison.Ordinal))
            {
                baseUser = baseUser.Substring(0, baseUser.Length - 2);
            }

            user = baseUser;

            if (normalizedServer == "lid")
            {
                domainType = DomainTypeLid;
            }
            else if (normalizedServer == "hosted")
            {
                domainType = DomainTypeHosted;
            }
            else if (normalizedServer == "hosted.lid")
            {
                domainType = DomainTypeHostedLid;
            }
            else if (agentValue != 0)
            {
                domainType = agentValue;
            }

            return true;
        }

        /// <summary>
        /// Returns the base JID (user@server) without device identifier.
        /// </summary>
        public static string GetBaseJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;
            if (jid.Contains("@g.us")) return jid; // Groups don't have device suffixes

            if (!TryDecodeJid(jid, out var user, out var server, out _, out _))
            {
                return jid;
            }

            return $"{user}@{server}";
        }

        /// <summary>
        /// Normalizes a device JID for storage/lookup by stripping any agent suffix and
        /// collapsing legacy ".0" device artifacts. Device id 0 is treated as "no device".
        /// </summary>
        public static string NormalizeDeviceJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return jid;
            if (jid.Contains("@g.us")) return jid;

            if (!TryDecodeJid(jid, out var user, out var server, out var device, out _))
            {
                return jid;
            }

            if (device.HasValue && device.Value > 0)
            {
                return $"{user}:{device.Value}@{server}";
            }

            return $"{user}@{server}";
        }

        public static void JidDecode(string jid, out string user, out string server)
        {
            if (string.IsNullOrEmpty(jid))
            {
                user = null;
                server = null;
                return;
            }
            
            var parts = jid.Split('@');
            if (parts.Length == 2)
            {
                user = parts[0];
                server = parts[1];
            }
            else
            {
                user = jid;
                server = S_WHATSAPP_NET;
            }
        }

        /// <summary>
        /// Decodes a JID into user, server, and device components
        /// JID format: user:device@server (e.g., "447768613172:17@s.whatsapp.net")
        /// </summary>
        public static void JidDecode(string jid, out string user, out string server, out int device)
        {
            device = 0;

            if (string.IsNullOrEmpty(jid))
            {
                user = null;
                server = null;
                return;
            }

            if (!TryDecodeJid(jid, out user, out server, out var parsedDevice, out _))
            {
                user = jid;
                server = S_WHATSAPP_NET;
                return;
            }

            if (parsedDevice.HasValue)
            {
                device = parsedDevice.Value;
            }
        }
    }
}
