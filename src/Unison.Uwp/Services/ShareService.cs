using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.Core;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// System share UI for a local cached media file.
    /// </summary>
    public sealed class ShareService : IShareService
    {
        private readonly object _sync = new object();
        private TaskCompletionSource<bool> _pending;
        private string _title;
        private StorageFile _file;

        public async Task ShareLocalFileAsync(string title, string localUriOrPath)
        {
            StorageFile file = await ResolveLocalFileAsync(localUriOrPath).ConfigureAwait(true);
            if (file == null)
            {
                throw new InvalidOperationException("Arquivo local da imagem não encontrado.");
            }

            var tcs = new TaskCompletionSource<bool>();
            lock (_sync)
            {
                _pending = tcs;
                _title = string.IsNullOrWhiteSpace(title) ? file.Name : title.Trim();
                _file = file;
            }

            DataTransferManager dtm = DataTransferManager.GetForCurrentView();
            dtm.DataRequested -= OnDataRequested;
            dtm.DataRequested += OnDataRequested;

            try
            {
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal,
                    () => DataTransferManager.ShowShareUI());

                await tcs.Task.ConfigureAwait(true);
            }
            finally
            {
                dtm.DataRequested -= OnDataRequested;
                lock (_sync)
                {
                    _pending = null;
                    _file = null;
                    _title = null;
                }
            }
        }

        private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            StorageFile file;
            string title;
            TaskCompletionSource<bool> tcs;
            lock (_sync)
            {
                file = _file;
                title = _title;
                tcs = _pending;
            }

            if (file == null)
            {
                args.Request.FailWithDisplayText("Nenhuma imagem para partilhar.");
                tcs?.TrySetResult(false);
                return;
            }

            DataRequestDeferral deferral = args.Request.GetDeferral();
            try
            {
                args.Request.Data.Properties.Title = title ?? file.Name;
                args.Request.Data.SetStorageItems(new IStorageItem[] { file });
                tcs?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                args.Request.FailWithDisplayText(ex.Message);
                tcs?.TrySetException(ex);
            }
            finally
            {
                deferral.Complete();
            }
        }

        internal static async Task<StorageFile> ResolveLocalFileAsync(string uriOrPath)
        {
            if (string.IsNullOrWhiteSpace(uriOrPath))
            {
                return null;
            }

            string value = uriOrPath.Trim();
            try
            {
                if (value.StartsWith("ms-appdata:///", StringComparison.OrdinalIgnoreCase))
                {
                    return await StorageFile.GetFileFromApplicationUriAsync(new Uri(value));
                }

                if (value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                {
                    string path = Uri.UnescapeDataString(new Uri(value).LocalPath);
                    return await StorageFile.GetFileFromPathAsync(path);
                }

                if (value.IndexOf(':') >= 0 || value.StartsWith("\\", StringComparison.Ordinal))
                {
                    return await StorageFile.GetFileFromPathAsync(value);
                }

                // Relative under LocalFolder (e.g. "media/images/foo.jpg")
                return await ApplicationData.Current.LocalFolder.GetFileAsync(value.Replace('/', '\\'));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ShareService] Resolve: " + ex.Message);
                return null;
            }
        }
    }
}
