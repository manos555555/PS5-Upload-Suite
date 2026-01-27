#nullable enable
namespace PS5UploadMobile.Services
{
    public class ConnectionManager
    {
        private static ConnectionManager? _instance;
        private static readonly object _lock = new object();

        public string IpAddress { get; set; } = "192.168.0.160";
        public int Port { get; set; } = 9113;
        public MainPage? MainPageReference { get; set; }

        private ConnectionManager() { }

        public static ConnectionManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new ConnectionManager();
                        }
                    }
                }
                return _instance;
            }
        }

        public void SetConnection(string ipAddress, int port)
        {
            IpAddress = ipAddress;
            Port = port;
        }

        public void SetMainPage(MainPage mainPage)
        {
            MainPageReference = mainPage;
        }
    }
}
