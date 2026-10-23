// =============================================================================
// FatalStreamCodeFromException
//
// Prefer a typed WaConnectionException reason; otherwise read the message text
// through FatalStreamCodeText. Used when OnError carries logout/replace codes.
// =============================================================================
using System;
using Unison.Socket.Session;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FatalStreamCodeFromException
    {
        public static string TryRead(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                var connectionError = current as WaConnectionException;
                if (connectionError != null)
                {
                    return ((int)connectionError.Reason).ToString();
                }
            }

            return FatalStreamCodeText.Read(ex?.Message);
        }
    }
}
