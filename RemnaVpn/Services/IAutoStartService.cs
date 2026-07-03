namespace RemnaVpn.Services
{
    public interface IAutoStartService
    {
        void SetAutoStart(bool enable);
        bool IsAutoStartEnabled();
    }
}
