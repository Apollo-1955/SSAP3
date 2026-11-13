using System.Threading;
using System.Windows;

namespace SSAP3.App;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\SSAP3.Tray.Instance.v1";
    private const string ShowWindowEventName = @"Local\SSAP3.Tray.Show.v1";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowRegistration;
    private MainWindow? _mainWindow;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        _instanceMutex = new Mutex(
            initiallyOwned: true,
            InstanceMutexName,
            out var createdNew);
        _showWindowEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ShowWindowEventName);

        if (!createdNew)
        {
            _showWindowEvent.Set();
            Shutdown();
            return;
        }

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();

        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindowEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    Dispatcher.BeginInvoke(
                        () => _mainWindow?.ShowFromTray());
                }
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void ExitApplication()
    {
        _mainWindow?.PrepareForExit();
        Shutdown();
    }

    private void App_OnExit(object sender, ExitEventArgs e)
    {
        _showWindowRegistration?.Unregister(null);
        _showWindowEvent?.Dispose();
        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // A secondary instance never owns the mutex.
            }

            _instanceMutex.Dispose();
        }
    }
}
