using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Core.Contracts;
using Unison.Uwp.Client;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Adapts SessionLogger to ISessionLogger (ViewModels) and IProtocolLogger (Baileys).
    /// </summary>
    public class SessionLoggerAdapter : ISessionLogger, IProtocolLogger
    {
        private readonly SessionLogger _logger;

        public SessionLoggerAdapter() : this(SessionLogger.Instance) { }

        public SessionLoggerAdapter(SessionLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool Enabled
        {
            get => _logger.Enabled;
            set => _logger.Enabled = value;
        }

        public bool PairingTraceActive
        {
            get => _logger.PairingTraceActive;
            set => _logger.PairingTraceActive = value;
        }

        public event EventHandler<string> OnLogUpdated
        {
            add => _logger.OnLogUpdated += value;
            remove => _logger.OnLogUpdated -= value;
        }

        public string GetLogText() => _logger.GetLogText();

        public void Clear() => _logger.Clear();

        public void WriteAlways(string message) => _logger.WriteAlways(message);

        public void WriteErrorAlways(string message, Exception ex = null)
            => _logger.WriteErrorAlways(message, ex);

        public async Task SaveToFileAsync()
        {
            await _logger.SaveToFileAsync();
        }

        public void LogKeyInfo(string title, Dictionary<string, string> values)
            => _logger.LogKeyInfo(title, values);
    }
}
