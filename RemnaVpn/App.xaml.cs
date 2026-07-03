using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RemnaVpn.Services;
using RemnaVpn.ViewModels;

namespace RemnaVpn
{
    public partial class App : System.Windows.Application
    {
        private ICryptographyService? _cryptographyService;
        private ISystemProxyService? _systemProxyService;
        private ISettingsService? _settingsService;
        private IAutoStartService? _autoStartService;
        private IRemnawaveService? _remnawaveService;
        private IXrayService? _xrayService;
        private ILocalizationService? _localizationService;
        private ITrayService? _trayService;

        private Mutex? _instanceMutex;
        private CancellationTokenSource? _pipeCts;
        private bool _isFirstInstance;
        private const string MutexName = "RemnaVpn_SingleInstance_Mutex";
        private const string PipeName = "RemnaVpn_Deeplink_Pipe";

        public MainViewModel MainViewModel { get; private set; } = null!;
        public ITrayService TrayService { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
            _isFirstInstance = createdNew;
            if (!createdNew)
            {
                // Another instance is already running! Send deeplink URL over NamedPipe if present
                if (e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0]))
                {
                    try
                    {
                        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None);
                        client.Connect(2000);
                        using var writer = new StreamWriter(client);
                        writer.WriteLine(e.Args[0]);
                        writer.Flush();
                    }
                    catch { }
                }
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Register protocol handlers (goatvpn://, remnawave://, etc.)
            ProtocolService.RegisterProtocolHandlers();

            // 1. Initialize services
            _cryptographyService = new CryptographyService();
            _systemProxyService = new SystemProxyService();
            
            // Critical Graceful Shutdown/Reliability: 
            // Clear any stale registry proxy settings on cold startup
            _systemProxyService.DisableProxy();

            _settingsService = new SettingsService(_cryptographyService);
            _autoStartService = new AutoStartService();
            _remnawaveService = new RemnawaveService(_cryptographyService!);
            _xrayService = new XrayService(_systemProxyService);
            _localizationService = new LocalizationService();
            _trayService = new TrayService(_xrayService);
            TrayService = _trayService;

            // 2. Initialize ViewModels
            MainViewModel = new MainViewModel(_xrayService, _settingsService, _remnawaveService, _autoStartService, _localizationService, _cryptographyService);

            // Start listening for deeplinks from secondary instances
            _pipeCts = new CancellationTokenSource();
            StartPipeListener(_pipeCts.Token);

            // If started via deeplink on cold startup, process immediately
            if (e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0]))
            {
                _ = MainViewModel.HandleDeeplinkAsync(e.Args[0]);
            }

            // 3. Register crash event handlers for emergency proxy cleanup
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private void StartPipeListener(CancellationToken token)
        {
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await server.WaitForConnectionAsync(token);
                        using var reader = new StreamReader(server);
                        string? url = await reader.ReadLineAsync(token);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (MainWindow != null)
                                {
                                    if (MainWindow.WindowState == WindowState.Minimized)
                                        MainWindow.WindowState = WindowState.Normal;
                                    MainWindow.Show();
                                    MainWindow.Activate();
                                }
                                _ = MainViewModel.HandleDeeplinkAsync(url);
                            });
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { await Task.Delay(1000, token); }
                }
            }, token);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            _pipeCts?.Cancel();
            _pipeCts?.Dispose();
            if (_isFirstInstance && _instanceMutex != null)
            {
                try { _instanceMutex.ReleaseMutex(); } catch { }
                _instanceMutex.Dispose();
            }

            // Graceful shutdown on normal close
            _trayService?.Dispose();
            if (_xrayService != null)
            {
                await _xrayService.DisconnectAsync();
            }
            if (_systemProxyService != null)
            {
                _systemProxyService.DisableProxy();
            }
            base.OnExit(e);
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // Clean up registry if the background threads crash
            _systemProxyService?.DisableProxy();
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // Clean up registry if the main thread crashes
            _systemProxyService?.DisableProxy();
        }
    }
}
