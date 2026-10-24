// =============================================================================
// IHandleDecryptedMessageHost
//
// Side effects for HandleDecryptedMessageDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IHandleDecryptedMessageHost
    {
        void SetIncomingMessagePumpStage(string stage, IncomingDecryptedNotifyRequest request);

        void LogLiveInboundHandle(IncomingDecryptedNotifyRequest request);

        bool HasMessageFacade { get; }

        void WriteMessageFacadeMissing(string messageId);

        Task NotifyIncomingDecryptedAsync(IncomingDecryptedNotifyRequest request);

        void LogHandleDecryptedError(Exception error, IncomingDecryptedNotifyRequest request);

        void RecordHandleDecryptedFailed(Exception error, IncomingDecryptedNotifyRequest request);

        string IncomingPumpStage { get; }
    }
}
