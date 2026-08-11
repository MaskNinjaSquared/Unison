using System;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace Unison.Background
{
    internal sealed class BackgroundNotificationContent
    {
        public string Title { get; set; }
        public string Preview { get; set; }
        public string ChatJid { get; set; }
        public bool IsRealMessage { get; set; }
        /// <summary>
        /// Toast appLogoOverride src (ms-appdata / ms-appx). Prefer local avatar; else placeholder.
        /// </summary>
        public string AvatarSrc { get; set; }
        public bool IsGroup { get; set; }
    }

    /// <summary>
    /// UI-free sender/preview normalization shared by the app and external task.
    /// A future minimal Noise/Signal decoder can pass a resolved message directly
    /// to this type without loading App, pages or XAML collections.
    /// </summary>
    internal static class BackgroundPreviewResolver
    {
        public const string ContactAvatarPlaceholder =
            "ms-appx:///Assets/Toast/avatar_contact.png";
        public const string GroupAvatarPlaceholder =
            "ms-appx:///Assets/Toast/avatar_group.png";

        public static BackgroundNotificationContent ResolveRealMessage(
            string chatJid,
            string chatName,
            string senderName,
            string preview,
            bool isGroup,
            string avatarUrl = null)
        {
            string safeChatName = string.IsNullOrWhiteSpace(chatName)
                ? BackgroundStrings.Get("Toast_NewMessageFallback", "Nova mensagem")
                : chatName.Trim();
            string safeSender = string.IsNullOrWhiteSpace(senderName)
                ? string.Empty
                : senderName.Trim();
            string body = NormalizePreview(preview);

            // Group: title = group name; body = "Author: message".
            // Direct: title = contact; body = message only.
            if (isGroup &&
                !string.IsNullOrWhiteSpace(safeSender) &&
                !string.Equals(safeSender, safeChatName, StringComparison.OrdinalIgnoreCase))
            {
                body = safeSender + ": " + body;
            }

            return new BackgroundNotificationContent
            {
                Title = safeChatName,
                Preview = body,
                ChatJid = chatJid ?? string.Empty,
                IsRealMessage = true,
                IsGroup = isGroup,
                AvatarSrc = ResolveAvatarSrc(avatarUrl, isGroup)
            };
        }

        /// <summary>
        /// Prefer a packaged/local avatar URI that toast can load; otherwise contact/group placeholder.
        /// Remote http(s) URLs are not used (toast would need download first).
        /// </summary>
        public static string ResolveAvatarSrc(string avatarUrl, bool isGroup)
        {
            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                string trimmed = avatarUrl.Trim();
                if (trimmed.StartsWith("ms-appdata://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("ms-appx://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed;
                }
            }

            return isGroup ? GroupAvatarPlaceholder : ContactAvatarPlaceholder;
        }

        public static BackgroundNotificationContent CreateGenericFallback()
        {
            return new BackgroundNotificationContent
            {
                Title = BackgroundStrings.Get(
                    "Toast_GenericFallbackTitle",
                    "Nova atividade no WhatsApp"),
                Preview = BackgroundStrings.Get(
                    "Toast_GenericFallbackBody",
                    "Abra o Unison para carregar a mensagem."),
                ChatJid = string.Empty,
                IsRealMessage = false,
                AvatarSrc = ContactAvatarPlaceholder,
                IsGroup = false
            };
        }

        /// <summary>
        /// Same idea as chat-list strip: remove Unison media placeholders and
        /// show localized kind labels (Foto / Adesivo / Áudio / Documento…),
        /// never the raw <c>[Image]</c> / <c>[Sticker]</c> tags.
        /// </summary>
        public static string NormalizePreview(string preview)
        {
            string value = string.IsNullOrWhiteSpace(preview)
                ? string.Empty
                : preview;
            value = value.Replace("\r\n", " ")
                         .Replace("\n", " ")
                         .Replace("\r", " ");

            string label;
            if (TryConsumeMediaTag(ref value, out label))
            {
                value = CollapseSpaces(value).Trim();
                if (string.IsNullOrEmpty(value))
                {
                    value = label;
                }
                else if (!string.IsNullOrEmpty(label))
                {
                    value = label + ": " + value;
                }
            }
            else
            {
                value = CollapseSpaces(value).Trim();
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = BackgroundStrings.Get("Toast_MediaPlaceholder", "[Mídia]");
            }

            if (value.Length > 180)
            {
                value = value.Substring(0, 177) + "...";
            }

            return value;
        }

        /// <summary>
        /// Recognizes FG English tags (<c>[Image]</c>) and BG localized tags
        /// (<c>[Foto]</c> / <c>[Figurinha]</c>) from preview extractors.
        /// Longer tags first so <c>[Voice Message]</c> wins over <c>[Voice]</c>.
        /// </summary>
        private static bool TryConsumeMediaTag(ref string value, out string label)
        {
            label = null;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            // resourceKey null → use fallback only (no ChatList_* key yet).
            var rules = new[]
            {
                new MediaTagRule(
                    new[] { "[Voice Message]", "[Mensagem de voz]" },
                    "ChatList_PreviewVoice",
                    "Mensagem de voz"),
                new MediaTagRule(
                    new[] { "[Scheduled Call]", "[Chamada agendada]" },
                    null,
                    "Chamada agendada"),
                new MediaTagRule(
                    new[] { "[Image]", "[Foto]" },
                    "ChatList_PreviewPhoto",
                    "Foto"),
                new MediaTagRule(
                    new[] { "[Video]", "[Vídeo]" },
                    "ChatList_PreviewVideo",
                    "Vídeo"),
                new MediaTagRule(
                    new[] { "[Sticker]", "[Figurinha]", "[Adesivo]" },
                    "ChatList_PreviewSticker",
                    "Adesivo"),
                new MediaTagRule(
                    new[] { "[Audio]", "[Áudio]" },
                    "ChatList_PreviewVoice",
                    "Áudio"),
                new MediaTagRule(
                    new[] { "[Document]", "[Documento]" },
                    "ChatList_PreviewDocument",
                    "Documento"),
                new MediaTagRule(
                    new[] { "[Reaction]", "[Reação]" },
                    "ChatList_PreviewReaction",
                    "Reação"),
                new MediaTagRule(
                    new[] { "[Contact]", "[Contato]" },
                    null,
                    "Contato"),
                new MediaTagRule(
                    new[] { "[Contacts]", "[Contatos]" },
                    null,
                    "Contatos"),
                new MediaTagRule(
                    new[] { "[Location]", "[Localização]" },
                    null,
                    "Localização"),
                new MediaTagRule(
                    new[] { "[Poll]", "[Enquete]" },
                    null,
                    "Enquete"),
                new MediaTagRule(
                    new[] { "[Call]", "[Chamada]" },
                    null,
                    "Chamada"),
                new MediaTagRule(
                    new[] { "[Message]", "[Nova mensagem]" },
                    "Toast_NewMessageFallback",
                    "Nova mensagem"),
            };

            for (int i = 0; i < rules.Length; i++)
            {
                MediaTagRule rule = rules[i];
                for (int t = 0; t < rule.Tags.Length; t++)
                {
                    if (!ConsumeTag(ref value, rule.Tags[t]))
                    {
                        continue;
                    }

                    label = string.IsNullOrEmpty(rule.ResourceKey)
                        ? rule.Fallback
                        : BackgroundStrings.Get(rule.ResourceKey, rule.Fallback);
                    return true;
                }
            }

            return false;
        }

        private static bool ConsumeTag(ref string s, string tag)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(tag))
            {
                return false;
            }

            int idx = s.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            s = s.Substring(0, idx) + s.Substring(idx + tag.Length);
            return true;
        }

        private static string CollapseSpaces(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            var chars = s.ToCharArray();
            int w = 0;
            bool prevSpace = false;
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool isSpace = c == ' ' || c == '\t';
                if (isSpace)
                {
                    if (prevSpace)
                    {
                        continue;
                    }

                    prevSpace = true;
                    chars[w++] = ' ';
                }
                else
                {
                    prevSpace = false;
                    chars[w++] = c;
                }
            }

            return new string(chars, 0, w);
        }

        private sealed class MediaTagRule
        {
            public MediaTagRule(string[] tags, string resourceKey, string fallback)
            {
                Tags = tags;
                ResourceKey = resourceKey;
                Fallback = fallback;
            }

            public string[] Tags { get; }
            public string ResourceKey { get; }
            public string Fallback { get; }
        }
    }

    internal static class BackgroundToastPresenter
    {
        private const string ToastGroup = "unison-bg";
        private const string ReconnectToastTag = "connection";
        private const string ReconnectToastActiveSetting =
            "UnisonReconnectToastActive";

        public static bool ShowGenericFallback(out string error)
        {
            return ShowGenericFallback(null, out error);
        }

        public static bool ShowGenericFallback(
            string replacementTag,
            out string error)
        {
            BackgroundNotificationContent content =
                BackgroundPreviewResolver.CreateGenericFallback();
            return ShowTemplateToast(
                content.Title,
                content.Preview,
                replacementTag,
                out error);
        }

        public static bool ShowReconnectRequired(out string error)
        {
            bool alreadyActive;
            return ShowReconnectRequired(out alreadyActive, out error);
        }

        public static bool ShowReconnectRequired(
            out bool alreadyActive,
            out string error)
        {
            alreadyActive = IsReconnectToastActive();
            if (alreadyActive)
            {
                error = string.Empty;
                return false;
            }

            bool shown = ShowTemplateToast(
                BackgroundStrings.Get(
                    "Toast_DisconnectedTitle",
                    "Unison desconectado"),
                BackgroundStrings.Get(
                    "Toast_DisconnectedBody",
                    "Abra o aplicativo para restaurar a conexão com o WhatsApp."),
                ReconnectToastTag,
                out error);
            if (shown)
            {
                SetReconnectToastActive(true);
            }
            return shown;
        }

        public static bool ClearReconnectRequired(out string error)
        {
            error = string.Empty;
            try
            {
                ToastNotificationManager.History.Remove(
                    ReconnectToastTag,
                    ToastGroup);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":0x" +
                        ex.HResult.ToString("X8");
                return false;
            }
            finally
            {
                SetReconnectToastActive(false);
            }
        }

        public static bool ShowRealMessage(
            BackgroundNotificationContent content,
            out string error)
        {
            return ShowRealMessage(content, null, out error);
        }

        public static bool ShowRealMessage(
            BackgroundNotificationContent content,
            string replacementTag,
            out string error)
        {
            error = string.Empty;
            if (content == null || !content.IsRealMessage)
            {
                error = "InvalidRealMessageContent";
                return false;
            }

            try
            {
                string launch = BuildLaunchArgument(content.ChatJid);
                string avatarSrc = string.IsNullOrWhiteSpace(content.AvatarSrc)
                    ? BackgroundPreviewResolver.ResolveAvatarSrc(null, content.IsGroup)
                    : content.AvatarSrc;
                string xml =
                    "<toast launch=\"" + EscapeXml(launch) + "\">" +
                    "<visual><binding template=\"ToastGeneric\">" +
                    "<text>" + EscapeXml(content.Title) + "</text>" +
                    "<text>" + EscapeXml(content.Preview) + "</text>" +
                    "<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"" +
                    EscapeXml(avatarSrc) + "\"/>" +
                    "</binding></visual>" +
                    "<audio src=\"ms-winsoundevent:Notification.IM\"/>" +
                    "</toast>";

                var document = new XmlDocument();
                document.LoadXml(xml);
                var toast = new ToastNotification(document)
                {
                    ExpirationTime = DateTimeOffset.UtcNow.AddHours(12)
                };
                ApplyReplacementIdentity(toast, replacementTag);
                ToastNotificationManager.CreateToastNotifier().Show(toast);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":0x" + ex.HResult.ToString("X8");
                return false;
            }
        }

        private static bool ShowTemplateToast(
            string title,
            string body,
            string replacementTag,
            out string error)
        {
            error = string.Empty;
            try
            {
                XmlDocument xml = ToastNotificationManager.GetTemplateContent(
                    ToastTemplateType.ToastText02);
                XmlNodeList nodes = xml.GetElementsByTagName("text");
                nodes[0].AppendChild(xml.CreateTextNode(
                    title ?? BackgroundStrings.Get("Toast_AppName", "Unison")));
                nodes[1].AppendChild(xml.CreateTextNode(body ?? string.Empty));
                var toast = new ToastNotification(xml);
                ApplyReplacementIdentity(toast, replacementTag);
                ToastNotificationManager.CreateToastNotifier().Show(toast);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":0x" + ex.HResult.ToString("X8");
                return false;
            }
        }

        private static void ApplyReplacementIdentity(
            ToastNotification toast,
            string replacementTag)
        {
            if (toast == null || string.IsNullOrWhiteSpace(replacementTag))
            {
                return;
            }

            // Tag/group replacement is supported by the Windows 10 Mobile target.
            // Keep each value within the platform's 16-character limit.
            string safeTag = replacementTag.Trim();
            if (safeTag.Length > 16)
            {
                safeTag = safeTag.Substring(safeTag.Length - 16);
            }
            toast.Tag = safeTag;
            toast.Group = ToastGroup;
        }

        private static bool IsReconnectToastActive()
        {
            try
            {
                object value =
                    ApplicationData.Current.LocalSettings.Values[
                        ReconnectToastActiveSetting];
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }

        private static void SetReconnectToastActive(bool active)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[
                    ReconnectToastActiveSetting] = active;
            }
            catch
            {
            }
        }

        private static string BuildLaunchArgument(string chatJid)
        {
            return string.IsNullOrWhiteSpace(chatJid)
                ? "notification=1"
                : "notification=1&chat=" + Uri.EscapeDataString(chatJid);
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}
