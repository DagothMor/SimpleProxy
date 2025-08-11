using System;
using System.IO;
using System.Net;
using System.Text;
using SimpleProxy;

namespace PuppyProxy
{
    /// <summary>
    /// System settings.
    /// </summary>
    public class Settings
    {
        public SettingsProxy Proxy
        {
            get
            {
                return _Proxy;
            }
            set
            {
                if (value == null) _Proxy = new SettingsProxy();
                else _Proxy = value;
            }
        }

        private SettingsProxy _Proxy = new SettingsProxy();
        public static Settings FromFile(string filename)
        {
            if (String.IsNullOrEmpty(filename)) throw new ArgumentNullException(nameof(filename));

            Settings ret = new Settings();

            if (!File.Exists(filename))
            {
                Console.WriteLine("Creating default configuration in " + filename);
                File.WriteAllBytes(filename, Encoding.UTF8.GetBytes(Common.SerializeJson(ret, true)));
                return ret;
            }
            else
            {
                ret = Common.DeserializeJson<Settings>(File.ReadAllBytes(filename));
                return ret;
            }
        }
    }
    public class SettingsProxy
    {
        private const int DEFAULT_PORT = 8000;
        private const string DEFAULT_IP_ADDRESS = "127.0.0.1";

        public int _ListenerPort = DEFAULT_PORT;
        public byte MaxThreads = byte.MaxValue;
        public string _ListenerIpAddress = DEFAULT_IP_ADDRESS;

        public ushort ListenerPort;
        public string ListenerIpAddress
        {
            get
            {
                return _ListenerIpAddress;
            }
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(ListenerIpAddress));
                _ListenerIpAddress = IPAddress.Parse(value).ToString();
            }
        }
        
    }
}
