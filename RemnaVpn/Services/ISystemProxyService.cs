namespace RemnaVpn.Services
{
    public interface ISystemProxyService
    {
        void EnableProxy(string host, int port);
        void DisableProxy();
        bool IsProxyEnabled();
    }
}
