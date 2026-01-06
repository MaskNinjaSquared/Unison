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
        
        public static string JidEncode(string user, string server = S_WHATSAPP_NET)
        {
            return $"{user}@{server}";
        }

        /// <summary>
        /// Returns the base JID (user@server) without device identifier.
        /// </summary>
        public static string GetBaseJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;
            if (jid.Contains("@g.us")) return jid; // Groups don't have device/dot suffixes
            
            var parts = jid.Split('@');
            if (parts.Length != 2) return jid;
            
            // User part: user[:device][.agent]
            // We want the bare 'user'
            var user = parts[0].Split(':')[0].Split('.')[0];
            return $"{user}@{parts[1]}";
        }

        /// <summary>
        /// Normalizes a device JID by stripping agent suffixes (.X) but keeping the device part.
        /// Example: "447768613172:17.0@s.whatsapp.net" -> "447768613172:17@s.whatsapp.net"
        /// </summary>
        public static string NormalizeDeviceJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return jid;
            if (jid.Contains("@g.us")) return jid;

            var parts = jid.Split('@');
            if (parts.Length != 2) return jid;

            var server = parts[1];
            var userPortion = parts[0];

            // userPortion can be user:device.agent or user.agent
            var colonParts = userPortion.Split(':');
            if (colonParts.Length == 2)
            {
                var user = colonParts[0].Split('.')[0];
                var device = colonParts[1].Split('.')[0]; // Strip .agent from device too
                return $"{user}:{device}@{server}";
            }
            else
            {
                var user = userPortion.Split('.')[0];
                return $"{user}@{server}";
            }
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
            
            var atParts = jid.Split('@');
            if (atParts.Length == 2)
            {
                server = atParts[1];
                // Check for user:device format
                var userPart = atParts[0];
                var colonParts = userPart.Split(':');
                if (colonParts.Length == 2)
                {
                    user = colonParts[0].Split('.')[0];
                    var devicePart = colonParts[1].Split('.')[0];
                    int.TryParse(devicePart, out device);
                }
                else
                {
                    user = userPart.Split('.')[0];
                }
            }
            else
            {
                user = jid;
                server = S_WHATSAPP_NET;
            }
        }
    }
}
