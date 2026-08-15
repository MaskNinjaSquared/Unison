using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace Unison.Background
{
    internal static class BrokerLog
    {
        public static async Task AppendAsync(string source, string message)
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    SocketBrokerConstants.BrokerLogFile,
                    CreationCollisionOption.OpenIfExists);
                string line = DateTime.UtcNow.ToString("O") + " | " +
                              (source ?? "broker") + " | " +
                              (message ?? string.Empty) + Environment.NewLine;
                await FileIO.AppendTextAsync(file, line);
            }
            catch
            {
            }
        }
    }
}
