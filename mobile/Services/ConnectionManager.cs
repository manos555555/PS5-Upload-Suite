#nullable enable
using System;
using System.Threading.Tasks;
using PS5Upload;

namespace PS5UploadMobile.Services
{
    public class ConnectionManager
    {
        private static ConnectionManager? _instance;
        private static readonly object _lock = new object();

        public string IpAddress { get; set; } = "192.168.0.160";
        public int Port { get; set; } = 9113;
        public MainPage? MainPageReference { get; set; }

        // Shared protocol instance used by all pages
        private PS5Protocol? _protocol;
        public PS5Protocol? Protocol => _protocol;
        public bool IsConnected => _protocol?.IsConnected ?? false;

        public event Action? ConnectionStateChanged;

        private ConnectionManager() { }

        public static ConnectionManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock) { if (_instance == null) _instance = new ConnectionManager(); }
                }
                return _instance;
            }
        }

        public void SetConnection(string ipAddress, int port)
        {
            IpAddress = ipAddress;
            Port = port;
        }

        public void SetMainPage(MainPage mainPage) => MainPageReference = mainPage;

        public async Task<bool> ConnectAsync(string? ip = null, int? port = null)
        {
            if (!string.IsNullOrWhiteSpace(ip)) IpAddress = ip!;
            if (port.HasValue) Port = port.Value;

            try { _protocol?.Dispose(); } catch { }
            _protocol = new PS5Protocol();
            bool ok = await _protocol.ConnectAsync(IpAddress, Port);
            if (!ok)
            {
                try { _protocol.Dispose(); } catch { }
                _protocol = null;
            }
            ConnectionStateChanged?.Invoke();
            return ok;
        }

        public void Disconnect()
        {
            try { _protocol?.Dispose(); } catch { }
            _protocol = null;
            ConnectionStateChanged?.Invoke();
        }

        /// <summary>
        /// Ensure the shared protocol is connected, attempt reconnect if not.
        /// </summary>
        public async Task<PS5Protocol?> EnsureConnectedAsync()
        {
            if (_protocol != null && _protocol.IsConnected) return _protocol;
            return await ConnectAsync() ? _protocol : null;
        }
    }
}
