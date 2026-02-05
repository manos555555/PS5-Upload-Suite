#nullable enable
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PS5UploadMobile.Services
{
    public class FtpService
    {
        private string _ps5Ip = "192.168.0.160";
        private int _ftpPort = 2121;
        private TcpClient? _controlClient;
        private NetworkStream? _controlStream;
        private StreamReader? _controlReader;

        public event EventHandler<string>? LogMessage;
        public event EventHandler<int>? ProgressChanged;

        public string PS5Ip
        {
            get => _ps5Ip;
            set => _ps5Ip = value;
        }

        public int FtpPort
        {
            get => _ftpPort;
            set => _ftpPort = value;
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                _controlClient = new TcpClient();
                await _controlClient.ConnectAsync(_ps5Ip, _ftpPort);
                _controlStream = _controlClient.GetStream();
                _controlReader = new StreamReader(_controlStream, Encoding.ASCII);

                // Read welcome message
                string? response = await _controlReader.ReadLineAsync();
                LogMessage?.Invoke(this, $"Connected: {response}");

                // Login as anonymous
                await SendCommandAsync("USER anonymous");
                await SendCommandAsync("PASS anonymous");
                await SendCommandAsync("TYPE I"); // Binary mode

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"Connection failed: {ex.Message}");
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                if (_controlClient?.Connected == true)
                {
                    await SendCommandAsync("QUIT");
                }
            }
            catch { }
            finally
            {
                _controlReader?.Dispose();
                _controlStream?.Dispose();
                _controlClient?.Dispose();
            }
        }

        public async Task<List<string>> ListDirectoryAsync(string path)
        {
            var files = new List<string>();

            try
            {
                // Change to directory
                await SendCommandAsync($"CWD {path}");

                // Enter passive mode
                string pasvResponse = await SendCommandAsync("PASV");
                var dataEndpoint = ParsePasvResponse(pasvResponse);

                // Connect to data channel
                using var dataClient = new TcpClient();
                await dataClient.ConnectAsync(dataEndpoint.Address, dataEndpoint.Port);
                using var dataStream = dataClient.GetStream();
                using var dataReader = new StreamReader(dataStream, Encoding.ASCII);

                // Send LIST command
                await SendCommandAsync("LIST");

                // Read directory listing
                string? line;
                while ((line = await dataReader.ReadLineAsync()) != null)
                {
                    files.Add(line);
                }

                dataClient.Close();
                await ReadResponseAsync(); // Read final response
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"List failed: {ex.Message}");
            }

            return files;
        }

        public async Task<bool> UploadFileAsync(string localPath, string remotePath)
        {
            try
            {
                var fileInfo = new FileInfo(localPath);
                long totalBytes = fileInfo.Length;
                long uploadedBytes = 0;

                // Enter passive mode
                string pasvResponse = await SendCommandAsync("PASV");
                var dataEndpoint = ParsePasvResponse(pasvResponse);

                // Connect to data channel
                using var dataClient = new TcpClient();
                await dataClient.ConnectAsync(dataEndpoint.Address, dataEndpoint.Port);
                using var dataStream = dataClient.GetStream();

                // Send STOR command
                await SendCommandAsync($"STOR {remotePath}");

                // Upload file
                using var fileStream = File.OpenRead(localPath);
                byte[] buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await dataStream.WriteAsync(buffer, 0, bytesRead);
                    uploadedBytes += bytesRead;

                    int progress = (int)((uploadedBytes * 100) / totalBytes);
                    ProgressChanged?.Invoke(this, progress);
                }

                dataClient.Close();
                await ReadResponseAsync(); // Read final response

                LogMessage?.Invoke(this, $"Upload complete: {Path.GetFileName(localPath)}");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"Upload failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DownloadFileAsync(string remotePath, string localPath)
        {
            try
            {
                // Get file size
                string sizeResponse = await SendCommandAsync($"SIZE {remotePath}");
                long totalBytes = ParseSizeResponse(sizeResponse);
                long downloadedBytes = 0;

                // Enter passive mode
                string pasvResponse = await SendCommandAsync("PASV");
                var dataEndpoint = ParsePasvResponse(pasvResponse);

                // Connect to data channel
                using var dataClient = new TcpClient();
                await dataClient.ConnectAsync(dataEndpoint.Address, dataEndpoint.Port);
                using var dataStream = dataClient.GetStream();

                // Send RETR command
                await SendCommandAsync($"RETR {remotePath}");

                // Download file
                using var fileStream = File.Create(localPath);
                byte[] buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await dataStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        int progress = (int)((downloadedBytes * 100) / totalBytes);
                        ProgressChanged?.Invoke(this, progress);
                    }
                }

                dataClient.Close();
                await ReadResponseAsync(); // Read final response

                LogMessage?.Invoke(this, $"Download complete: {Path.GetFileName(remotePath)}");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"Download failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DeleteFileAsync(string remotePath)
        {
            try
            {
                await SendCommandAsync($"DELE {remotePath}");
                LogMessage?.Invoke(this, $"Deleted: {remotePath}");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"Delete failed: {ex.Message}");
                return false;
            }
        }

        private async Task<string> SendCommandAsync(string command)
        {
            if (_controlStream == null || _controlReader == null)
                throw new InvalidOperationException("Not connected");

            byte[] commandBytes = Encoding.ASCII.GetBytes(command + "\r\n");
            await _controlStream.WriteAsync(commandBytes, 0, commandBytes.Length);

            return await ReadResponseAsync();
        }

        private async Task<string> ReadResponseAsync()
        {
            if (_controlReader == null)
                throw new InvalidOperationException("Not connected");

            string? response = await _controlReader.ReadLineAsync();
            LogMessage?.Invoke(this, $"FTP: {response}");
            return response ?? "";
        }

        private IPEndPoint ParsePasvResponse(string response)
        {
            // Parse PASV response: 227 Entering Passive Mode (192,168,0,160,8,19)
            int start = response.IndexOf('(');
            int end = response.IndexOf(')');
            string[] parts = response.Substring(start + 1, end - start - 1).Split(',');

            string ip = $"{parts[0]}.{parts[1]}.{parts[2]}.{parts[3]}";
            int port = int.Parse(parts[4]) * 256 + int.Parse(parts[5]);

            return new IPEndPoint(IPAddress.Parse(ip), port);
        }

        private long ParseSizeResponse(string response)
        {
            // Parse SIZE response: 213 12345
            string[] parts = response.Split(' ');
            return parts.Length > 1 ? long.Parse(parts[1]) : 0;
        }
    }
}
