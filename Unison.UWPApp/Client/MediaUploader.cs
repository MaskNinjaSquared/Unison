using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using System.IO;
using System.Threading;
using System.Text;
using Newtonsoft.Json;
using System.Diagnostics;
using Unison.UWPApp.Protocol; // For BinaryNode

namespace Unison.UWPApp.Client
{
    public class MediaUploader
    {
        private SocketClient _socket;
        
        // Hardcoded fallbacks if dynamic query fails
        private static readonly string[] DEFAULT_HOSTS = new[] { "mmg.whatsapp.net" };
        private const string MEDIA_PATH_IMAGE = "/mms/image";
        
        // Cache media conn
        private MediaConnInfo _cachedMediaConn;
        private DateTime _mediaConnPoc = DateTime.MinValue;

        public class MediaConnInfo
        {
            public string Auth { get; set; }
            public int Ttl { get; set; }
            public List<string> Hosts { get; set; } = new List<string>();
            public DateTime FetchTime { get; set; }
        }

        public class MediaUploadResult
        {
            public string Url { get; set; }
            public string DirectPath { get; set; }
            public string MediaKey { get; set; }
            public byte[] FileSha256 { get; set; }
            public byte[] FileEncSha256 { get; set; }
            public byte[] MediaKeyBytes { get; set; }
            public string MimeType { get; set; }
            public long FileLength { get; set; }
        }

        public MediaUploader(SocketClient socket)
        {
            _socket = socket;
        }

        public async Task<MediaConnInfo> GetMediaConnectionAsync(bool force = false)
        {
            if (!force && _cachedMediaConn != null && (DateTime.Now - _cachedMediaConn.FetchTime).TotalSeconds < _cachedMediaConn.Ttl)
            {
                Debug.WriteLine("[MediaUploader] Using cached media connection");
                return _cachedMediaConn;
            }

            // Send IQ query to get media conn
            // <iq type='set' xmlns='w:m' to='s.whatsapp.net'>
            //   <media_conn/>
            // </iq>
            
            Debug.WriteLine("[MediaUploader] Querying media connection...");

            var msgId = _socket.GenerateMessageId();
            var node = new BinaryNode("iq")
            {
                Attrs = new Dictionary<string, string>
                {
                    { "to", "s.whatsapp.net" },
                    { "type", "set" },
                    { "xmlns", "w:m" },  // Correct xmlns for media_conn
                    { "id", msgId }
                },
                Content = new List<BinaryNode>
                {
                    new BinaryNode("media_conn")
                }
            };

            Debug.WriteLine($"[MediaUploader] Sending media_conn query with id={msgId}");
            
            try
            {
                var response = await _socket.SendIqAsync(node, 15000); // 15 second timeout
                
                Debug.WriteLine($"[MediaUploader] Got response: {response?.Tag}");

                // Parse response
                var mediaConnNode = response?.GetChild("media_conn");
                
                if (mediaConnNode != null)
                {
                    var info = new MediaConnInfo
                    {
                        Auth = mediaConnNode.GetAttribute("auth"),
                        Ttl = int.Parse(mediaConnNode.GetAttribute("ttl") ?? "3600"),
                        FetchTime = DateTime.Now
                    };

                    Debug.WriteLine($"[MediaUploader] Got auth token: {info.Auth?.Substring(0, Math.Min(20, info.Auth?.Length ?? 0))}...");

                    // Get hosts
                    if (mediaConnNode.Content is List<BinaryNode> children)
                    {
                        foreach (var child in children)
                        {
                            if (child.Tag == "host")
                            {
                                var hostname = child.GetAttribute("hostname");
                                if (!string.IsNullOrEmpty(hostname))
                                {
                                    info.Hosts.Add(hostname);
                                    Debug.WriteLine($"[MediaUploader] Found host: {hostname}");
                                }
                            }
                        }
                    }
                    
                    // Fallback if no hosts found
                    if (info.Hosts.Count == 0)
                    {
                        Debug.WriteLine("[MediaUploader] No hosts in response, using defaults");
                        info.Hosts.AddRange(DEFAULT_HOSTS);
                    }
                    
                    _cachedMediaConn = info;
                    return info;
                }
            }
            catch (TimeoutException)
            {
                Debug.WriteLine("[MediaUploader] Media connection query timed out, using fallback");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaUploader] Media connection query failed: {ex.Message}");
            }

            // Fallback: Use default hosts without auth
            // Note: This may not work for actual uploads but allows debugging
            Debug.WriteLine("[MediaUploader] Using fallback media connection (no auth)");
            _cachedMediaConn = new MediaConnInfo
            {
                Auth = "", // Will likely fail but let's try
                Ttl = 60,
                FetchTime = DateTime.Now,
                Hosts = new List<string>(DEFAULT_HOSTS)
            };
            return _cachedMediaConn;
        }

        public async Task<MediaUploadResult> UploadImageAsync(byte[] imageBytes)
        {
            // 1. Encrypt
            var encrypted = await MediaUtils.EncryptMediaAsync(imageBytes, "image");

            // 2. Get Media Connection (hosts & auth)
            var mediaConn = await GetMediaConnectionAsync();
            if (mediaConn == null) throw new Exception("Failed to get media connection");

            // 3. Prepare Upload
            // URL: https://{host}/mms/image/{enc_sha256_b64}?auth={auth}&token={enc_sha256_b64}
            // Headers: Origin: https://web.whatsapp.com
            
            string encSha256B64 = Convert.ToBase64String(encrypted.FileEncSha256)
                .Replace("+", "-").Replace("/", "_").Replace("=", ""); // Url safe base64
            
            string authEncoded = System.Net.WebUtility.UrlEncode(mediaConn.Auth);
            
            foreach (var host in mediaConn.Hosts)
            {
                try
                {
                    string url = $"https://{host}{MEDIA_PATH_IMAGE}/{encSha256B64}?auth={authEncoded}&token={encSha256B64}";
                    
                    using (var client = new HttpClient())
                    {
                        client.DefaultRequestHeaders.Add("Origin", "https://web.whatsapp.com");
                        // User-Agent is optional but good practice
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("UnisonUWP/1.0");

                        using (var content = new ByteArrayContent(encrypted.EncryptedBytes))
                        {
                            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                            
                            var response = await client.PostAsync(url, content);
                            var json = await response.Content.ReadAsStringAsync();

                            if (response.IsSuccessStatusCode)
                            {
                                // Parse JSON result
                                dynamic result = JsonConvert.DeserializeObject(json);
                                
                                return new MediaUploadResult
                                {
                                    Url = result.url,
                                    DirectPath = result.direct_path,
                                    MediaKey = Convert.ToBase64String(encrypted.MediaKey),
                                    MediaKeyBytes = encrypted.MediaKey,
                                    FileSha256 = encrypted.FileSha256,
                                    FileEncSha256 = encrypted.FileEncSha256,
                                    FileLength = encrypted.FileLength,
                                    MimeType = "image/jpeg" 
                                    // Note: Assuming jpeg for now as we might convert/resize, 
                                    // or just respect input. For now let's say input is jpeg or png.
                                };
                            }
                            else 
                            {
                                Debug.WriteLine($"[MediaUploader] Upload failed to {host}: {response.StatusCode} {json}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaUploader] Error uploading to {host}: {ex.Message}");
                }
            }

            throw new Exception("Media upload failed on all hosts");
        }
    }
}
