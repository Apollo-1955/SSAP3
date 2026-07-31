using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SoundStage.Core;
using SoundStage.Diagnostics;
using SoundStage.Platform.Windows;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Drawing = System.Drawing;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using WinForms = System.Windows.Forms;

namespace SoundStage.App;

public partial class MainWindow : Window
{
    private readonly WindowsBleAirPodsScanner _scanner = new();
    private readonly WindowsSpatialAudioController _spatialAudio = new();
    private readonly HeadTrackingAudioStateWriter _headTrackingAudioState = new();
    private readonly SemaphoreSlim _driverGate = new(1, 1);
    private readonly HeadTrackingFrameParser _headTrackingParser = new();
    private readonly AirPodsBatteryPacketParser _batteryPacketParser = new();
    private readonly SoundStageUserSettings _settings = SoundStageUserSettings.Load();
    private readonly WinForms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _connectionTimer;

    private SoundStageDriverAacpClient? _driverClient;
    private CancellationTokenSource? _motionCancellation;
    private string? _airPodsAddress;
    private bool _aacpInitialized;
    private bool _allowClose;
    private bool _uiReady;
    private bool _isAnimatingOut;
    private long _animationGeneration;
    private float _smoothedYaw;
    private float _smoothedPitch;
    private float _trackingCenterYaw;
    private float _trackingCenterPitch;
    private int _trackingCenterSamples;
    private DateTime _ignoreDeactivationUntil;
    private DateTime _lastControlRepairAttempt;
    private DateTime _lastAdvertisementReceived;
    private bool _airPodsClassicConnected;
    private bool _repairInProgress;
    private bool _connectionRefreshInProgress;
    private bool _motionReaderRunning;

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = CreateTrayIcon();
        _connectionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        _connectionTimer.Tick += ConnectionTimer_OnTick;
        _scanner.AdvertisementReceived += Scanner_OnAdvertisementReceived;
        _scanner.StatusChanged += Scanner_OnStatusChanged;
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;
        Deactivated += MainWindow_OnDeactivated;
        SoundStageAppLog.Write("SoundStage 0.4.0 process started.");
    }

    public void ShowFromTray()
    {
        _animationGeneration++;
        _isAnimatingOut = false;
        PopupRoot.BeginAnimation(OpacityProperty, null);
        if (!IsVisible)
        {
            Show();
        }

        PositionNearTray();
        ShowInTaskbar = false;
        _ignoreDeactivationUntil = DateTime.UtcNow.AddSeconds(1);
        Activate();
        Topmost = true;
        AnimateIn();
    }

    public void ToggleFromTray()
    {
        if (IsVisible)
        {
            HideToTray();
            return;
        }

        ShowFromTray();
    }

    public void PrepareForExit()
    {
        if (_allowClose)
        {
            return;
        }

        _allowClose = true;
        _motionCancellation?.Cancel();
        _trayIcon.Visible = false;
        Close();
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionNearTray();
        _ignoreDeactivationUntil = DateTime.UtcNow.AddSeconds(1);
        ApplySavedNoiseMode();
        UpdateCaseBattery(level: null, charging: false);
        LoadRememberedAirPods();
        StartScanner();
        _uiReady = true;
        AnimateIn();
        _connectionTimer.Start();
        await RepairControlPathAsync(force: false);
        await RefreshSpatialStatusAsync();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _motionCancellation?.Cancel();
        _motionCancellation?.Dispose();
        _connectionTimer.Stop();
        _connectionTimer.Tick -= ConnectionTimer_OnTick;
        _scanner.AdvertisementReceived -= Scanner_OnAdvertisementReceived;
        _scanner.StatusChanged -= Scanner_OnStatusChanged;
        _scanner.Dispose();
        _driverClient?.Dispose();
        _headTrackingAudioState.Dispose();
        _driverGate.Dispose();
        _trayIcon.Dispose();
        SoundStageAppLog.Write("SoundStage process stopped.");
    }

    private async void MainWindow_OnDeactivated(object? sender, EventArgs e)
    {
        await Task.Delay(120);
        var activationGrace =
            _ignoreDeactivationUntil - DateTime.UtcNow;
        if (activationGrace > TimeSpan.Zero)
        {
            await Task.Delay(activationGrace);
        }

        if (!_allowClose &&
            IsVisible &&
            !IsActive &&
            DateTime.UtcNow >= _ignoreDeactivationUntil)
        {
            HideToTray();
        }
    }

    private void WindowSurface_OnMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        LoadRememberedAirPods();
        await _scanner.RestartAsync();
        await RepairControlPathAsync(force: true);
        _ = RefreshSpatialStatusAsync();
        AnimateRefreshButton();
    }

    private void HideButton_OnClick(object sender, RoutedEventArgs e) =>
        HideToTray();

    private void ExitButton_OnClick(object sender, RoutedEventArgs e) =>
        ((App)System.Windows.Application.Current).ExitApplication();

    private async void RepairButton_OnClick(object sender, RoutedEventArgs e) =>
        await RepairControlPathAsync(force: true);

    private async void ReportButton_OnClick(object sender, RoutedEventArgs e)
    {
        var reportButton = sender as System.Windows.Controls.Button;
        if (reportButton is not null)
        {
            reportButton.IsEnabled = false;
        }

        StatusLog.Text = "Collecting diagnostics…";
        try
        {
            var result = await SoundStageDiagnosticCollector.CollectAsync();
            StatusLog.Text = result.Complete
                ? "Diagnostic ZIP created"
                : "Diagnostic ZIP created with warnings";
            SoundStageAppLog.Write($"Diagnostic bundle created: {result.ZipPath}");
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{result.ZipPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            StatusLog.Text = $"Report failed: {Shorten(exception.Message, 45)}";
            SoundStageAppLog.Write($"Diagnostic collection failed: {exception}");
        }
        finally
        {
            if (reportButton is not null)
            {
                reportButton.IsEnabled = true;
            }
        }
    }

    private async void NoiseModeRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady ||
            sender is not RadioButton { Tag: string tag } ||
            !Enum.TryParse<NoiseControlMode>(tag, out var mode))
        {
            return;
        }

        await SetNoiseModeAsync(mode);
    }

    private async Task SetNoiseModeAsync(NoiseControlMode mode)
    {
        SetNoiseControlsEnabled(false);
        NoiseStatusText.Text = "Sending…";
        NoiseStatusText.Foreground = (Brush)FindResource("MutedTextBrush");
        try
        {
            await SendAacpPacketAsync(AacpPacketFactory.CreateNoiseControl(mode));
            SelectNoiseMode(mode);
            _settings.NoiseMode = mode.ToString();
            _settings.Save();
            NoiseStatusText.Text = GetNoiseModeLabel(mode);
            NoiseStatusText.Foreground = (Brush)FindResource("SuccessBrush");
            StatusLog.Text = $"AirPods mode changed · {DateTime.Now:T}";
            _trayIcon.Text = $"SoundStage · {GetNoiseModeLabel(mode)}";
        }
        catch (Exception exception)
        {
            ResetAacpConnection();
            NoiseStatusText.Text = "Not connected";
            NoiseStatusText.Foreground = Brushes.Orange;
            StatusLog.Text = Shorten(exception.Message, 62);
        }
        finally
        {
            SetNoiseControlsEnabled(true);
        }
    }

    private async void SpatialModeRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady ||
            sender is not RadioButton { Tag: string tag } ||
            !Enum.TryParse<SoundStageSpatialMode>(tag, out var mode))
        {
            return;
        }

        await SetSpatialModeAsync(mode);
    }

    private async Task SetSpatialModeAsync(SoundStageSpatialMode mode)
    {
        SetSpatialControlsEnabled(false);
        SpatialStatusText.Text = "Configuring the audio pipeline…";
        try
        {
            if (mode == SoundStageSpatialMode.Off)
            {
                await StopMotionTrackingAsync();
                _headTrackingAudioState.SetMode(SoundStageSpatialEngineMode.Off);
                await DisableWindowsSonicIfEnabledAsync();
                ApplySpatialResult(
                    CreateSoundStageSpatialStatus(enabled: false),
                    SoundStageSpatialMode.Off);
                return;
            }

            if (!HeadTrackingAudioStateWriter.IsEngineInstalled ||
                !_headTrackingAudioState.IsAvailable)
            {
                _headTrackingAudioState.SetMode(SoundStageSpatialEngineMode.Off);
                ApplySpatialResult(
                    new SpatialAudioStatus(
                        Available: true,
                        Enabled: false,
                        EndpointId: string.Empty,
                        EndpointName: "AirPods Stereo",
                        Message: _headTrackingAudioState.LastError is null
                            ? "Spatial engine is not installed. Run SoundStage Setup again."
                            : $"Spatial engine unavailable: {_headTrackingAudioState.LastError}"),
                    SoundStageSpatialMode.Off);
                return;
            }

            // SoundStage performs its own binaural rendering. Keeping Windows
            // Sonic enabled here would process the signal twice.
            await DisableWindowsSonicIfEnabledAsync();
            if (mode == SoundStageSpatialMode.Fixed)
            {
                await StopMotionTrackingAsync();
                _headTrackingAudioState.SetMode(SoundStageSpatialEngineMode.Fixed);
                ApplySpatialResult(
                    CreateSoundStageSpatialStatus(enabled: true),
                    SoundStageSpatialMode.Fixed);
                return;
            }

            _headTrackingAudioState.SetMode(SoundStageSpatialEngineMode.Tracked);
            ApplySpatialResult(
                CreateSoundStageSpatialStatus(enabled: true),
                SoundStageSpatialMode.Tracked);
            await StartMotionTrackingAsync();
        }
        catch (Exception exception)
        {
            SpatialStatusText.Text = Shorten(exception.Message, 76);
            SpatialStatusText.Foreground = Brushes.Orange;
            StatusLog.Text = "Spatial Audio error";
        }
        finally
        {
            SetSpatialControlsEnabled(true);
        }
    }

    private async Task RefreshSpatialStatusAsync()
    {
        await DisableWindowsSonicIfEnabledAsync();
        if (HeadTrackingAudioStateWriter.IsEngineInstalled &&
            _headTrackingAudioState.IsAvailable &&
            Enum.TryParse<SoundStageSpatialMode>(
                _settings.SpatialMode,
                ignoreCase: true,
                out var savedMode))
        {
            if (savedMode == SoundStageSpatialMode.Tracked)
            {
                _headTrackingAudioState.SetMode(SoundStageSpatialEngineMode.Tracked);
                ApplySpatialResult(
                    CreateSoundStageSpatialStatus(enabled: true),
                    SoundStageSpatialMode.Tracked,
                    save: false);
                await StartMotionTrackingAsync();
                return;
            }

            if (savedMode == SoundStageSpatialMode.Fixed)
            {
                _headTrackingAudioState.SetMode(SoundStageSpatialEngineMode.Fixed);
                ApplySpatialResult(
                    CreateSoundStageSpatialStatus(enabled: true),
                    SoundStageSpatialMode.Fixed,
                    save: false);
                return;
            }
        }

        _headTrackingAudioState.SetMode(SoundStageSpatialEngineMode.Off);
        var status = CreateSoundStageSpatialStatus(enabled: false);
        ApplySpatialResult(status, SoundStageSpatialMode.Off, save: false);
    }

    private async Task DisableWindowsSonicIfEnabledAsync()
    {
        var status = await _spatialAudio.GetStatusAsync();
        if (status.Enabled)
        {
            _ = await _spatialAudio.SetWindowsSonicAsync(false);
        }
    }

    private static SpatialAudioStatus CreateSoundStageSpatialStatus(bool enabled) =>
        new(
            Available: true,
            Enabled: enabled,
            EndpointId: string.Empty,
            EndpointName: "AirPods Stereo",
            Message: enabled
                ? "SoundStage binaural engine active"
                : "Spatial audio is off");

    private void ApplySpatialResult(
        SpatialAudioStatus status,
        SoundStageSpatialMode mode,
        bool save = true)
    {
        _uiReady = false;
        SpatialOffRadio.IsChecked = mode == SoundStageSpatialMode.Off;
        SpatialFixedRadio.IsChecked = mode == SoundStageSpatialMode.Fixed;
        SpatialTrackedRadio.IsChecked = mode == SoundStageSpatialMode.Tracked;
        _uiReady = true;

        SpatialStatusText.Text = mode switch
        {
            SoundStageSpatialMode.Fixed when status.Enabled =>
                "SoundStage HRTF · fixed soundstage",
            SoundStageSpatialMode.Tracked when status.Enabled =>
                "SoundStage HRTF · starting head tracking",
            _ => status.Message,
        };
        SpatialStatusText.Foreground = status.Enabled
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("MutedTextBrush");
        MotionBadge.Text = mode == SoundStageSpatialMode.Tracked ? "SYNC" : "HRTF";

        if (save)
        {
            _settings.SpatialMode = mode.ToString();
            _settings.Save();
        }
    }

    private async Task StartMotionTrackingAsync()
    {
        await StopMotionTrackingAsync();
        _headTrackingParser.Reset();
        _smoothedYaw = 0;
        _smoothedPitch = 0;
        _trackingCenterYaw = 0;
        _trackingCenterPitch = 0;
        _trackingCenterSamples = 0;
        await SendAacpPacketAsync(AacpPacketFactory.CreateOwnsConnection(true));
        await Task.Delay(350);
        await SendAacpPacketAsync(
            AacpPacketFactory.CreateAlternateStartHeadTracking());

        _motionCancellation = new CancellationTokenSource();
        _ = ReadMotionLoopAsync(_motionCancellation.Token);
    }

    private async Task StopMotionTrackingAsync()
    {
        var cancellation = _motionCancellation;
        _motionCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
        try
        {
            await SendAacpPacketAsync(
                AacpPacketFactory.CreateAlternateStopHeadTracking());
        }
        catch
        {
            // Disconnecting while stopping the optional sensor stream is harmless.
        }

        MotionBadge.Text = "HRTF";
    }

    private async Task ReadMotionLoopAsync(CancellationToken cancellationToken)
    {
        _motionReaderRunning = true;
        var started = DateTime.UtcNow;
        var triedFallbackPacket = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] packet;
                try
                {
                    await _driverGate.WaitAsync(cancellationToken);
                    try
                    {
                        packet = _driverClient?.Receive() ?? [];
                    }
                    finally
                    {
                        _driverGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MotionBadge.Text = "IMU ×";
                        SpatialStatusText.Text =
                            $"Fixed HRTF active · sensor: {Shorten(exception.Message, 40)}";
                    });
                    return;
                }

                if (_headTrackingParser.TryParse(packet, out var frame))
                {
                    const int centerSampleCount = 12;
                    if (_trackingCenterSamples < centerSampleCount)
                    {
                        _trackingCenterSamples++;
                        _trackingCenterYaw += WrapDegrees(
                            frame.Yaw - _trackingCenterYaw) /
                            _trackingCenterSamples;
                        _trackingCenterPitch +=
                            (frame.Pitch - _trackingCenterPitch) /
                            _trackingCenterSamples;
                        _smoothedYaw = 0;
                        _smoothedPitch = 0;
                    }
                    else
                    {
                        var centeredYaw = WrapDegrees(
                            frame.Yaw - _trackingCenterYaw);
                        var centeredPitch =
                            frame.Pitch - _trackingCenterPitch;
                        var yawDelta = WrapDegrees(
                            centeredYaw - _smoothedYaw);
                        var pitchDelta = centeredPitch - _smoothedPitch;
                        var yawSmoothing = Math.Clamp(
                            0.12f + Math.Abs(yawDelta) * 0.018f,
                            0.12f,
                            0.42f);
                        var pitchSmoothing = Math.Clamp(
                            0.11f + Math.Abs(pitchDelta) * 0.016f,
                            0.11f,
                            0.34f);
                        _smoothedYaw += yawDelta * yawSmoothing;
                        _smoothedPitch += pitchDelta * pitchSmoothing;
                    }
                    _headTrackingAudioState.UpdateMotion(
                        _smoothedYaw,
                        _smoothedPitch);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MotionBadge.Text = "LIVE";
                        SpatialStatusText.Text = "Head tracking active";
                        SpatialStatusText.Foreground =
                            (Brush)FindResource("SuccessBrush");
                    });
                }
                else if (_batteryPacketParser.TryParse(packet, out var battery) &&
                         battery is not null)
                {
                    await Dispatcher.InvokeAsync(() => ApplyBatteryUpdate(battery));
                }
                else if (!triedFallbackPacket &&
                         DateTime.UtcNow - started > TimeSpan.FromSeconds(2))
                {
                    triedFallbackPacket = true;
                    try
                    {
                        await SendAacpPacketAsync(
                            AacpPacketFactory.CreateStartHeadTracking());
                    }
                    catch
                    {
                        // The primary packet is only a compatibility fallback.
                    }
                }
                else if (DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MotionBadge.Text = "HRTF";
                        SpatialStatusText.Text =
                            "Fixed HRTF active · waiting for AirPods motion";
                        SpatialStatusText.Foreground = Brushes.Orange;
                    });
                }

                await Task.Delay(18, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Mode changes and shutdown cancel the optional sensor stream.
        }
        finally
        {
            _motionReaderRunning = false;
        }
    }

    private async Task SendAacpPacketAsync(byte[] packet)
    {
        if (string.IsNullOrWhiteSpace(_airPodsAddress))
        {
            LoadRememberedAirPods();
        }

        var address = _airPodsAddress ??
            throw new InvalidOperationException(
                "AirPods were not found in remembered Bluetooth devices.");

        await _driverGate.WaitAsync();
        try
        {
            await EnsureAacpConnectionLockedAsync(address, allowRepair: true);
            var client = _driverClient ??
                throw new InvalidOperationException(
                    "The SoundStage control driver did not become available.");
            client.Send(packet);
        }
        finally
        {
            _driverGate.Release();
        }
    }

    private async Task EnsureAacpConnectionLockedAsync(
        string address,
        bool allowRepair)
    {
        if (_driverClient is null)
        {
            var connection = SoundStageDriverAacpClient.TryConnect(address);
            var firstError = connection.Message;
            if (connection.Connected && connection.Client is not null)
            {
                _driverClient = connection.Client;
            }
            else
            {
                connection.Dispose();
                if (!allowRepair)
                {
                    throw new IOException(firstError);
                }

                bool testSigningActive;
                try
                {
                    testSigningActive = WindowsCodeIntegrity.IsTestSigningActive;
                }
                catch (Exception exception)
                {
                    SoundStageAppLog.Write(
                        $"Code Integrity query failed before AAP repair: {exception.Message}");
                    testSigningActive = true;
                }

                if (!testSigningActive)
                {
                    throw new IOException(
                        "SoundStage driver is blocked because Windows Test Mode is off. " +
                        "Run SoundStage Setup repair and restart Windows.");
                }

                var repair = await Task.Run(() =>
                    WindowsAirPodsServiceManager.EnsureControlProfilesEnabled(
                        forceRebind: true));
                SoundStageAppLog.Write(
                    $"AAP repair: found={repair.AirPodsFound}; " +
                    $"connected={repair.Connected}; changed={repair.Changed}; " +
                    $"enabled={repair.AnyProfileEnabled}; {repair.Message}");
                if (!string.IsNullOrWhiteSpace(repair.Address))
                {
                    address = repair.Address;
                    _airPodsAddress = repair.Address;
                    DeviceAddress.Text = repair.Address;
                }

                if (!repair.AnyProfileEnabled)
                {
                    throw new IOException(repair.Message);
                }

                for (var attempt = 0; attempt < 12; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 250 : 400);
                    connection = SoundStageDriverAacpClient.TryConnect(address);
                    if (connection.Connected && connection.Client is not null)
                    {
                        _driverClient = connection.Client;
                        break;
                    }

                    firstError = connection.Message;
                    connection.Dispose();
                }

                if (_driverClient is null)
                {
                    throw new IOException(
                        $"{repair.Message} Driver interface did not return: {firstError}");
                }
            }

            _aacpInitialized = false;
        }

        if (!_aacpInitialized)
        {
            _driverClient.Send(AacpPacketFactory.CreateHandshake());
            _driverClient.Send(AacpPacketFactory.CreateFeatureFlags());
            _driverClient.Send(AacpPacketFactory.CreateNotificationRequest());
            _aacpInitialized = true;
            SoundStageAppLog.Write("AACP session initialized.");
        }
    }

    private async Task RepairControlPathAsync(bool force)
    {
        if (_repairInProgress ||
            (!force &&
             DateTime.UtcNow - _lastControlRepairAttempt < TimeSpan.FromSeconds(12)))
        {
            return;
        }

        _repairInProgress = true;
        _lastControlRepairAttempt = DateTime.UtcNow;
        RepairButton.IsEnabled = false;
        try
        {
            LoadRememberedAirPods();
            if (string.IsNullOrWhiteSpace(_airPodsAddress))
            {
                StatusLog.Text = "Pair AirPods in Windows first";
                return;
            }

            StatusLog.Text = force
                ? "Repairing the AirPods control profile…"
                : "Checking the AirPods control profile…";
            await _driverGate.WaitAsync();
            try
            {
                await EnsureAacpConnectionLockedAsync(
                    _airPodsAddress,
                    allowRepair: true);
            }
            finally
            {
                _driverGate.Release();
            }

            NoiseStatusText.Text = "AAP ready";
            NoiseStatusText.Foreground = (Brush)FindResource("SuccessBrush");
            StatusLog.Text = "AirPods control profile ready";
            SoundStageAppLog.Write("Control path health check passed.");
            if (SpatialTrackedRadio.IsChecked == true && !_motionReaderRunning)
            {
                await StartMotionTrackingAsync();
            }
        }
        catch (Exception exception)
        {
            ResetAacpConnection();
            NoiseStatusText.Text = "Not connected";
            NoiseStatusText.Foreground = Brushes.Orange;
            StatusLog.Text = Shorten(exception.Message, 66);
            SoundStageAppLog.Write($"Control path repair failed: {exception}");
        }
        finally
        {
            RepairButton.IsEnabled = true;
            _repairInProgress = false;
        }
    }

    private void ResetAacpConnection()
    {
        _driverClient?.Dispose();
        _driverClient = null;
        _aacpInitialized = false;
    }

    private void StartScanner()
    {
        try
        {
            _scanner.Start();
            if (!_airPodsClassicConnected)
            {
                ConnectionBadge.Text = "Scanning AirPods over BLE…";
                ConnectionDot.Fill = Brushes.Gold;
            }
        }
        catch (Exception exception)
        {
            ConnectionBadge.Text = "Bluetooth unavailable";
            ConnectionDot.Fill = Brushes.OrangeRed;
            StatusLog.Text = Shorten(exception.Message, 62);
            SoundStageAppLog.Write($"BLE scanner failed to start: {exception}");
        }
    }

    private void LoadRememberedAirPods()
    {
        try
        {
            var remembered = WindowsRememberedAirPodsFinder.FindBest();
            if (remembered is null)
            {
                _airPodsClassicConnected = false;
                return;
            }

            var addressChanged = !string.Equals(
                    _airPodsAddress,
                    remembered.Address,
                    StringComparison.OrdinalIgnoreCase);
            var connectionDropped = _airPodsClassicConnected && !remembered.Connected;
            var connectionRestored = !_airPodsClassicConnected && remembered.Connected;
            if (addressChanged || connectionDropped || connectionRestored)
            {
                ResetAacpConnection();
            }

            _airPodsAddress = remembered.Address;
            _airPodsClassicConnected = remembered.Connected;
            DeviceAddress.Text = remembered.Address;
            ConnectionBadge.Text = remembered.Connected
                ? "Connected · Stereo"
                : "Remembered · open the case";
            ConnectionDot.Fill = remembered.Connected
                ? (Brush)FindResource("SuccessBrush")
                : Brushes.Gold;
        }
        catch (Exception exception)
        {
            StatusLog.Text =
                $"Bluetooth: {Shorten(exception.Message, 52)}";
            SoundStageAppLog.Write($"Remembered AirPods lookup failed: {exception}");
        }
    }

    private async void ConnectionTimer_OnTick(object? sender, EventArgs e)
    {
        if (_connectionRefreshInProgress)
        {
            return;
        }

        _connectionRefreshInProgress = true;
        try
        {
            LoadRememberedAirPods();
            if (_airPodsClassicConnected && _driverClient is null)
            {
                await RepairControlPathAsync(force: false);
            }
            else if (!_airPodsClassicConnected &&
                     DateTime.UtcNow - _lastAdvertisementReceived >
                     TimeSpan.FromSeconds(8))
            {
                ConnectionBadge.Text = _airPodsAddress is null
                    ? "Scanning AirPods over BLE…"
                    : "Remembered · reconnecting";
                ConnectionDot.Fill = Brushes.Gold;
            }
        }
        finally
        {
            _connectionRefreshInProgress = false;
        }
    }

    private void Scanner_OnAdvertisementReceived(
        object? sender,
        AirPodsAdvertisementStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            _lastAdvertisementReceived = DateTime.UtcNow;
            DeviceModel.Text = status.Model;
            DeviceAddress.Text = _airPodsAddress ?? status.Address;
            SetBattery(LeftBatteryText, status.LeftBattery, status.IsLeftCharging);
            SetBattery(RightBatteryText, status.RightBattery, status.IsRightCharging);
            UpdateCaseBattery(status.CaseBattery, status.IsCaseCharging);
            ConnectionBadge.Text = _airPodsClassicConnected
                ? "Connected · Stereo"
                : "AirPods nearby · BLE";
            ConnectionDot.Fill = _airPodsClassicConnected
                ? (Brush)FindResource("SuccessBrush")
                : Brushes.Gold;
            StatusLog.Text =
                $"{(status.IsLeftInEar ? "L in ear" : "L out")} · " +
                $"{(status.IsRightInEar ? "R in ear" : "R out")} · " +
                $"{DateTime.Now:T}";
        });
    }

    private void ApplyBatteryUpdate(AirPodsBatteryUpdate update)
    {
        if (update.LeftLevel is not null)
        {
            SetBattery(
                LeftBatteryText,
                update.LeftLevel,
                update.IsLeftCharging);
        }
        if (update.RightLevel is not null)
        {
            SetBattery(
                RightBatteryText,
                update.RightLevel,
                update.IsRightCharging);
        }
        UpdateCaseBattery(update.CaseLevel, update.IsCaseCharging);
    }

    private void UpdateCaseBattery(int? level, bool charging)
    {
        if (level is not null)
        {
            _settings.LastCaseBattery = level;
            _settings.LastCaseCharging = charging;
            _settings.LastCaseBatteryObservedAt = DateTimeOffset.UtcNow;
            _settings.Save();
            SetBattery(CaseBatteryText, level, charging);
            CaseBatteryText.ToolTip = "Live case battery";
            return;
        }

        if (_settings.LastCaseBattery is not null &&
            _settings.LastCaseBatteryObservedAt is { } observedAt &&
            DateTimeOffset.UtcNow - observedAt < TimeSpan.FromDays(7))
        {
            SetBattery(
                CaseBatteryText,
                _settings.LastCaseBattery,
                _settings.LastCaseCharging);
            CaseBatteryText.ToolTip =
                $"Last seen {observedAt.LocalDateTime:g}. Open the case to refresh.";
            return;
        }

        CaseBatteryText.Text = "Open case";
        CaseBatteryText.ToolTip =
            "The case broadcasts its battery while it is open.";
    }

    private void Scanner_OnStatusChanged(object? sender, string status)
    {
        SoundStageAppLog.Write(status);
        Dispatcher.Invoke(() => StatusLog.Text = Shorten(status, 66));
    }

    private void ApplySavedNoiseMode()
    {
        if (!Enum.TryParse<NoiseControlMode>(
                _settings.NoiseMode,
                out var mode))
        {
            mode = NoiseControlMode.NoiseCancellation;
        }

        SelectNoiseMode(mode);
        NoiseStatusText.Text = GetNoiseModeLabel(mode);
    }

    private void SelectNoiseMode(NoiseControlMode mode)
    {
        _uiReady = false;
        NoiseOffRadio.IsChecked = mode == NoiseControlMode.Off;
        NoiseAncRadio.IsChecked = mode == NoiseControlMode.NoiseCancellation;
        NoiseTransparencyRadio.IsChecked = mode == NoiseControlMode.Transparency;
        NoiseAdaptiveRadio.IsChecked = mode == NoiseControlMode.Adaptive;
        _uiReady = true;
    }

    private void SetNoiseControlsEnabled(bool enabled)
    {
        NoiseOffRadio.IsEnabled = enabled;
        NoiseAncRadio.IsEnabled = enabled;
        NoiseTransparencyRadio.IsEnabled = enabled;
        NoiseAdaptiveRadio.IsEnabled = enabled;
    }

    private void SetSpatialControlsEnabled(bool enabled)
    {
        SpatialOffRadio.IsEnabled = enabled;
        SpatialFixedRadio.IsEnabled = enabled;
        SpatialTrackedRadio.IsEnabled = enabled;
    }

    private void HideToTray()
    {
        if (_isAnimatingOut || !IsVisible)
        {
            return;
        }

        _isAnimatingOut = true;
        var generation = ++_animationGeneration;
        var fade = new DoubleAnimation(
            PopupRoot.Opacity,
            0,
            TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseIn,
            },
        };
        fade.Completed += (_, _) =>
        {
            if (generation != _animationGeneration || !_isAnimatingOut)
            {
                return;
            }

            Hide();
            _isAnimatingOut = false;
        };
        PopupRoot.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateIn()
    {
        _animationGeneration++;
        _isAnimatingOut = false;
        PopupRoot.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PopupScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = ease,
            });
        PopupScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = ease,
            });
        PopupTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = ease,
            });
    }

    private void AnimateRefreshButton()
    {
        RefreshButton.RenderTransformOrigin = new Point(0.5, 0.5);
        var rotate = new RotateTransform();
        RefreshButton.RenderTransform = rotate;
        rotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(520))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseInOut,
                },
            });
    }

    private void PositionNearTray()
    {
        var cursor = WinForms.Cursor.Position;
        var screen = WinForms.Screen.FromPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = (screen.WorkingArea.Right / dpi.DpiScaleX) - Width - 8;
        Top = (screen.WorkingArea.Bottom / dpi.DpiScaleY) - Height - 8;
    }

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var icon = new WinForms.NotifyIcon
        {
            Icon = CreateSoundStageIcon(),
            Text = "SoundStage · AirPods",
            Visible = true,
        };
        icon.MouseDown += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    () =>
                {
                    _ignoreDeactivationUntil = DateTime.UtcNow.AddSeconds(1);
                    _animationGeneration++;
                    _isAnimatingOut = false;
                    PopupRoot.BeginAnimation(OpacityProperty, null);
                });
            }
        };
        icon.MouseUp += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    ToggleFromTray);
            }
        };

        var menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Drawing.Color.FromArgb(32, 34, 44),
            ForeColor = Drawing.Color.White,
        };
        menu.Items.Add(CreateTrayMenuItem(
            "Open SoundStage",
            (_, _) => Dispatcher.Invoke(ShowFromTray)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateTrayMenuItem(
            "ANC",
            (_, _) => Dispatcher.Invoke(
                async () => await SetNoiseModeAsync(
                    NoiseControlMode.NoiseCancellation))));
        menu.Items.Add(CreateTrayMenuItem(
            "Transparency",
            (_, _) => Dispatcher.Invoke(
                async () => await SetNoiseModeAsync(
                    NoiseControlMode.Transparency))));
        menu.Items.Add(CreateTrayMenuItem(
            "Adaptive",
            (_, _) => Dispatcher.Invoke(
                async () => await SetNoiseModeAsync(
                    NoiseControlMode.Adaptive))));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateTrayMenuItem(
            "Exit",
            (_, _) => Dispatcher.Invoke(
                () => ((App)System.Windows.Application.Current).ExitApplication())));
        icon.ContextMenuStrip = menu;
        return icon;
    }

    private static WinForms.ToolStripMenuItem CreateTrayMenuItem(
        string text,
        EventHandler handler)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        item.Click += handler;
        return item;
    }

    private static Drawing.Icon CreateSoundStageIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri(
                "pack://application:,,,/Assets/SoundStage.ico",
                UriKind.Absolute));
        if (resource is null)
        {
            return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        }

        using var stream = resource.Stream;
        using var icon = new Drawing.Icon(stream);
        return (Drawing.Icon)icon.Clone();
    }

    private static void SetBattery(
        TextBlock text,
        int? level,
        bool charging)
    {
        text.Text = level is null
            ? "—"
            : $"{level}%{(charging ? " ⚡" : string.Empty)}";
    }

    private static string GetNoiseModeLabel(NoiseControlMode mode) =>
        mode switch
        {
            NoiseControlMode.Off => "Off",
            NoiseControlMode.NoiseCancellation => "ANC",
            NoiseControlMode.Transparency => "Transparency",
            NoiseControlMode.Adaptive => "Adaptive",
            _ => mode.ToString(),
        };

    private static float WrapDegrees(float value)
    {
        while (value > 180)
        {
            value -= 360;
        }
        while (value < -180)
        {
            value += 360;
        }
        return value;
    }

    private static string Shorten(string value, int length) =>
        value.Length <= length ? value : $"{value[..(length - 1)]}…";

}
