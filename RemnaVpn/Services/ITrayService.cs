using System;

namespace RemnaVpn.Services
{
    public interface ITrayService : IDisposable
    {
        void Initialize(Action onRestoreWindow, Action onExitApplication);
        void ShowBalloonTip(int timeoutMs, string title, string text);
        void UpdateLanguage();
    }
}
