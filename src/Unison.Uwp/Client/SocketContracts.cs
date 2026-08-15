// =============================================================================
// SocketContracts
//
// The vocabulary the app and the connection share: the shapes handed across the
// IWhatsAppSocket boundary, plus the diagnostic sink everything writes to.
//
// These lived at the top of the legacy SocketClient and outlived it. They are
// not protocol types - Unison.Socket has its own, richer ones - but the app
// still speaks in these, so they stay until the last caller is converted.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Uwp.Services;

namespace Unison.Uwp.Client
{
    public static class DictionaryExtensions
    {
        public static TValue GetDictionaryValueOrDefault<TKey, TValue>(
            this IDictionary<TKey, TValue> dictionary,
            TKey key,
            TValue defaultValue = default(TValue))
        {
            return dictionary.TryGetValue(key, out var value) ? value : defaultValue;
        }
    }

    /// <summary>
    /// Envia diagnosticos para o depurador E para o SessionLogger.
    /// Debug.WriteLine sozinho so aparece com o depurador anexado -- inviavel no
    /// Windows 10 Mobile. Assim as mensagens ficam legiveis no proprio aparelho.
    /// </summary>
    internal static class Diag
    {
        public static void W(object message)
        {
            // Caminho rapido: se o log de sessao / pairing trace esta desligado E nao ha
            // depurador anexado, nao ha para onde escrever -- evita custo em caminhos quentes
            // (sao ~259 pontos de log, varios dentro do processamento de mensagens).
            bool logAtivo;
            try { logAtivo = SessionLogger.Instance.ShouldCaptureDiag; } catch { logAtivo = false; }
            bool depurador = System.Diagnostics.Debugger.IsAttached;
            if (!logAtivo && !depurador) return;

            var text = message?.ToString() ?? string.Empty;
            if (depurador) System.Diagnostics.Debug.WriteLine(text);
            if (logAtivo) { try { SessionLogger.Instance.Info(text); } catch { } }
        }

        /// <summary>Always visible on-device (pairing/QR). Prefer for milestones.</summary>
        public static void Always(object message)
        {
            var text = message?.ToString() ?? string.Empty;
            try { SessionLogger.Instance.WriteAlways(text); } catch { }
        }
    }

    /// <summary>
    /// Event args for decrypted incoming messages
    /// </summary>
    public class DecryptedMessageEventArgs : EventArgs
    {
        public string FromJid { get; set; }
        public string Participant { get; set; }  // Actual sender JID in group messages
        public string ParticipantAlt { get; set; } // PN/LID alternate supplied by modern WA envelopes
        public string AddressingMode { get; set; }
        public string MessageId { get; set; }
        public Proto.Message Message { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsFromMe { get; set; }
        public string PushName { get; set; }
        public string VerifiedName { get; set; }
        public string SenderLid { get; set; }
        public string PeerRecipientPn { get; set; }
        public string PeerRecipientLid { get; set; }
        public string RecipientJid { get; set; }
        public bool IsOffline { get; set; }
    }

    public class MissingMessageEventArgs : EventArgs
    {
        public string ChatJid { get; set; }
        public string Participant { get; set; }
        public string MessageId { get; set; }
        public bool IsFromMe { get; set; }
        public DateTime Timestamp { get; set; }
        public string Reason { get; set; }
    }

    public sealed class OutgoingMessageStatusEventArgs : EventArgs
    {
        public string MessageId { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
    }

    public sealed class DirtyNotificationEventArgs : EventArgs
    {
        public string Type { get; set; }
        public string Timestamp { get; set; }
    }

    public sealed class ProfilePictureResult
    {
        public string Url { get; set; }
        public string TargetJid { get; set; }
        public string TokenLookupJid { get; set; }
        public bool IsNotFound { get; set; }
        public bool IsTimeout { get; set; }
        public string FailureReason { get; set; }
    }
}
