using System;
using System.Drawing;
using System.Windows.Forms;
using RemnaVpn.Helpers;

namespace RemnaVpn.Services
{
    public class TrayService : ITrayService
    {
        private readonly IXrayService _xrayService;
        private NotifyIcon? _notifyIcon;
        private Action? _onRestoreWindow;
        private Action? _onExitApplication;

        public TrayService(IXrayService xrayService)
        {
            _xrayService = xrayService ?? throw new ArgumentNullException(nameof(xrayService));
        }

        public void Initialize(Action onRestoreWindow, Action onExitApplication)
        {
            _onRestoreWindow = onRestoreWindow ?? throw new ArgumentNullException(nameof(onRestoreWindow));
            _onExitApplication = onExitApplication ?? throw new ArgumentNullException(nameof(onExitApplication));

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = $"{LocalizationHelper.GetString("Str_AppTitle", "GoatWeb")} Client",
                Visible = true
            };

            _notifyIcon.DoubleClick += (s, e) => _onRestoreWindow?.Invoke();

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Show App", null, (s, e) => _onRestoreWindow?.Invoke());
            contextMenu.Items.Add("Disconnect VPN", null, async (s, e) =>
            {
                if (_xrayService.Status == "Connected" || _xrayService.Status == "Connecting")
                {
                    await _xrayService.DisconnectAsync();
                }
            });
            contextMenu.Items.Add("Exit", null, (s, e) => _onExitApplication?.Invoke());

            contextMenu.Opening += (s, e) => UpdateLanguage();
            _notifyIcon.ContextMenuStrip = contextMenu;

            UpdateLanguage();
        }

        public void ShowBalloonTip(int timeoutMs, string title, string text)
        {
            _notifyIcon?.ShowBalloonTip(timeoutMs, title, text, ToolTipIcon.Info);
        }

        public void UpdateLanguage()
        {
            if (_notifyIcon == null || _notifyIcon.ContextMenuStrip == null) return;

            string appTitle = LocalizationHelper.GetString("Str_AppTitle", "GoatWeb");
            _notifyIcon.Text = $"{appTitle} Client";

            var menu = _notifyIcon.ContextMenuStrip;
            if (menu.Items.Count >= 3)
            {
                menu.Items[0].Text = LocalizationHelper.GetString("Str_TrayShow", "Show App");
                menu.Items[1].Text = LocalizationHelper.GetString("Str_TrayDisconnect", "Disconnect VPN");
                menu.Items[2].Text = LocalizationHelper.GetString("Str_TrayExit", "Exit");
            }
        }

        public void Dispose()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            GC.SuppressFinalize(this);
        }
    }
}
