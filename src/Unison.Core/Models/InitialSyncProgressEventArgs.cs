using System;

namespace Unison.Core.Models
{
    public sealed class InitialSyncProgressEventArgs : EventArgs
    {
        public bool IsActive { get; set; }
        public bool IsCompleted { get; set; }
        public int ProcessedConversations { get; set; }
        public int TotalConversations { get; set; }
        public int VisibleChatTarget { get; set; }
        public string Stage { get; set; }

        public string GetDisplayText()
        {
            if (IsCompleted)
            {
                return "Sincronização concluída";
            }

            if (TotalConversations > 0)
            {
                return "Sincronizando conversas… " + ProcessedConversations + " de " + TotalConversations;
            }

            return "Sincronizando conversas… " + ProcessedConversations + " carregadas";
        }
    }
}
