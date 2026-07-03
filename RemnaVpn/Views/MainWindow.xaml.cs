using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using RemnaVpn.Helpers;
using RemnaVpn.Services;
using RemnaVpn.ViewModels;

namespace RemnaVpn.Views
{
    public partial class MainWindow : Window
    {
        private readonly ITrayService _trayService;
        private bool _isExplicitClose;

        public MainWindow()
        {
            InitializeComponent();
            
            var app = (App)System.Windows.Application.Current;
            DataContext = app.MainViewModel;
            _trayService = app.TrayService;

            _trayService.Initialize(RestoreWindow, ExitApplication);
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeButton_Click(sender, e);
            }
            else
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    DragMove();
                }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isExplicitClose)
            {
                e.Cancel = true;
                Hide();
                string title = LocalizationHelper.GetString("Str_AppTitle", "GoatWeb");
                string body = LocalizationHelper.GetString("Str_TrayBackgroundText", "RemnaVpn is running in the background.");
                _trayService.ShowBalloonTip(1500, title, body);
            }
            else
            {
                _trayService.Dispose();
            }
            base.OnClosing(e);
        }

        private void ExitApplication()
        {
            _isExplicitClose = true;
            System.Windows.Application.Current.Shutdown();
        }
    }
}
