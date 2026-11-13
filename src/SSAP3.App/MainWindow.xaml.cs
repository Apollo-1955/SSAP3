using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SSAP3.App.Services;
using SSAP3.Core;
using SSAP3.Core.Aacp;
using SSAP3.Core.Connection;
using SSAP3.Core.Conversation;
using SSAP3.Core.Motion;
using SSAP3.Core.Spatial;
using SSAP3.Diagnostics;
using SSAP3.Platform.Windows;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Drawing = System.Drawing;
using RadioButton = System.Windows.Controls.RadioButton;
using WinForms = System.Windows.Forms;

namespace SSAP3.App;

/// <summary>
/// SSAP3 Control Center tray panel. It only renders service snapshots and
/// forwards commands; all Bluetooth, motion and engine work lives in
/// <see cref="AirPodsService"/>.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan AacpBatteryFreshness = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AdvertisementFreshness = TimeSpan.FromSeconds(45);

    private readonly Ssap3UserSettings _settings = Ssap3UserSettings.Load();
    private readonly WindowsBleAirPodsScanner _scanner = new();
    private readonly WindowsSpatialAudioController _windowsSpatial = new();
    private readonly AdvertisementFilter _advertisementFilter = new();
    private readonly AirPodsService _service;
    private readonly WinForms.NotifyIcon _trayIcon;

    private ServiceSnapshot? _snapshot;
    private AirPodsAdvertisementStatus? _advertisement;
    private DateTimeOffset _advertisementAt;
    private string? _filterAddress;
    private bool _uiUpdating;
    private bool _modeRequestInFlight;
    private string? _modeResultText;
    private bool _allowClose;
    private bool _isAnimatingOut;
    private long _animationGeneration;
    private DateTime _ignoreDeactivationUntil;
    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();
        _service = new AirPodsService(_settings);
        _service.Updated += Service_OnUpdated;
        _trayIcon = CreateTrayIcon();
        _scanner.AdvertisementReceived += Scanner_OnAdvertisementReceived;
        _scanner.StatusChanged += Scanner_OnStatusChanged;
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;
        Deactivated += MainWindow_OnDeactivated;
        Ssap3AppLog.Write("SSAP3 Control Center started.");
    }

    internal AirPodsService Service => _service;

    internal Ssap3UserSettings Settings => _settings;

    internal ServiceSnapshot? LatestSnapshot => _snapshot;

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
        _trayIcon.Visible = false;
        _settingsWindow?.Close();
        Close();
    }

    private bool _initialized;

    /// <summary>Starts the service and scanner without showing the panel (sign-in autostart).</summary>
    public void InitializeInBackground() => Initialize();

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionNearTray();
        _ignoreDeactivationUntil = DateTime.UtcNow.AddSeconds(1);
        Initialize();
        AnimateIn();
    }

    private void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _uiUpdating = true;
        SelectSpatialMode(_settings.SpatialMode);
        ConversationToggle.IsChecked = _settings.ConversationAwarenessEnabled;
        _uiUpdating = false;
        _service.Start();
        StartScanner();
        if (_settings.SpatialMode != SpatialEngineMode.Off)
        {
            _ = DisableWindowsSonicIfEnabledAsync();
        }
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
        _scanner.AdvertisementReceived -= Scanner_OnAdvertisementReceived;
        _scanner.StatusChanged -= Scanner_OnStatusChanged;
        _scanner.Dispose();
        _service.Updated -= Service_OnUpdated;
        _service.Dispose();
        _settings.Save();
        _trayIcon.Dispose();
        Ssap3AppLog.Write("SSAP3 Control Center stopped.");
    }

    private async void MainWindow_OnDeactivated(object? sender, EventArgs e)
    {
        await Task.Delay(120);
        var grace = _ignoreDeactivationUntil - DateTime.UtcNow;
        if (grace > TimeSpan.Zero)
        {
            await Task.Delay(grace);
        }

        if (!_allowClose && IsVisible && !IsActive && DateTime.UtcNow >= _ignoreDeactivationUntil)
        {
            HideToTray();
        }
    }

    private void WindowSurface_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Service_OnUpdated(object? sender, ServiceSnapshot snapshot)
    {
        _snapshot = snapshot;
        RenderSnapshot(snapshot);
        _settingsWindow?.Render(snapshot);
    }

    private void RenderSnapshot(ServiceSnapshot snapshot)
    {
        _uiUpdating = true;
        try
        {
            var status = snapshot.Status;
            DeviceModel.Text = string.IsNullOrWhiteSpace(snapshot.DeviceName) ? "AirPods Pro 3" : snapshot.DeviceName;
            ConnectionTitle.Text = status.Title;
            ConnectionDetail.Text = status.Detail;
            ConnectionDot.Fill = SeverityBrush(status.Severity);

            var controlsReady = snapshot.Status.Kind is DeviceStatusKind.ConnectedNoMotion or DeviceStatusKind.MotionStarting
                or DeviceStatusKind.MotionStreaming or DeviceStatusKind.MotionStalled or DeviceStatusKind.MotionUnsupported;
            SetNoiseControlsEnabled(controlsReady && !_modeRequestInFlight);
            if (!_modeRequestInFlight)
            {
                SelectNoiseMode(snapshot.ReportedMode);
                NoiseStatusText.Text = !controlsReady
                    ? "Unavailable"
                    : _modeResultText ?? (snapshot.ReportedMode is { } mode ? $"{Label(mode)} (reported by AirPods)" : "Waiting for AirPods");
            }

            RenderBattery(snapshot);
            RenderSpatial(snapshot, controlsReady);
            RenderConversation(snapshot, controlsReady);
            UpdateTrayText(snapshot);

            if (!string.IsNullOrWhiteSpace(snapshot.Address) && snapshot.Address != _filterAddress)
            {
                _filterAddress = snapshot.Address;
                _advertisementFilter.PairedModelId = WindowsBluetoothIdentity.TryGetAppleProductId(snapshot.Address, out var productId)
                    ? AirPodsAdvertisementParser.ModelIdFromProductId(productId)
                    : null;
            }
        }
        finally
        {
            _uiUpdating = false;
        }
    }

    private void RenderBattery(ServiceSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        if (snapshot.AacpBattery is { } aacp && snapshot.AacpBatteryAt is { } at && now - at < AacpBatteryFreshness)
        {
            SetBattery(LeftBatteryText, aacp.LeftLevel, aacp.IsLeftCharging);
            SetBattery(RightBatteryText, aacp.RightLevel, aacp.IsRightCharging);
            UpdateCaseBattery(aacp.CaseLevel, aacp.IsCaseCharging);
            BatterySourceText.Text = $"Reported by the AirPods · {at.ToLocalTime():t}";
            return;
        }

        if (_advertisement is { } advert && now - _advertisementAt < AdvertisementFreshness)
        {
            SetBattery(LeftBatteryText, advert.LeftBattery, advert.IsLeftCharging);
            SetBattery(RightBatteryText, advert.RightBattery, advert.IsRightCharging);
            UpdateCaseBattery(advert.CaseBattery, advert.IsCaseCharging);
            BatterySourceText.Text = advert.IsIdentityResolved
                ? $"Bluetooth LE (verified) · {_advertisementAt.ToLocalTime():t}"
                : $"Nearby {advert.Model} (Bluetooth LE, not cryptographically verified) · {_advertisementAt.ToLocalTime():t}";
            return;
        }

        SetBattery(LeftBatteryText, null, false);
        SetBattery(RightBatteryText, null, false);
        UpdateCaseBattery(null, false);
        BatterySourceText.Text = "Battery unavailable: open the case near this PC.";
    }

    private void RenderSpatial(ServiceSnapshot snapshot, bool controlsReady)
    {
        SpatialFixedRadio.IsEnabled = snapshot.SpatialEngineInstalled;
        SpatialTrackedRadio.IsEnabled = snapshot.SpatialEngineInstalled;
        if (!snapshot.SpatialEngineInstalled)
        {
            SpatialStatusText.Text = "The system-wide engine is not installed. Enable it in SSAP3 Setup (opt-in).";
            MotionBadge.Text = "OFF";
        }
        else
        {
            SpatialStatusText.Text = snapshot.SpatialMode switch
            {
                SpatialEngineMode.Off => "Off: audio is passed through unchanged.",
                SpatialEngineMode.Fixed => "Virtual speakers in front of you.",
                _ when !controlsReady => "Head tracking needs the AAP driver: rendering a fixed soundstage.",
                _ => snapshot.Status.Kind switch
                {
                    DeviceStatusKind.MotionStreaming => "Speakers stay anchored to your monitor.",
                    DeviceStatusKind.MotionStalled => "Motion stalled: holding the stage, then easing to centre.",
                    DeviceStatusKind.MotionUnsupported => "No motion data from the AirPods: fixed soundstage.",
                    _ => "Starting head tracking…",
                },
            };
            MotionBadge.Text = snapshot.SpatialMode switch
            {
                SpatialEngineMode.Off => "OFF",
                SpatialEngineMode.Fixed => "FIXED",
                _ => snapshot.Motion == MotionStreamStatus.Streaming ? "LIVE" : "WAIT",
            };
        }

        var pose = snapshot.Pose;
        var tracking = snapshot.SpatialMode == SpatialEngineMode.Tracked && pose.Calibrated && snapshot.Motion != MotionStreamStatus.Inactive;
        PoseText.Text = tracking
            ? $"Yaw {pose.YawDegrees,6:+0.0;-0.0}°   Pitch {pose.PitchDegrees,6:+0.0;-0.0}°"
            : "Yaw —   Pitch —";
        MotionDetailText.Text = snapshot.Motion switch
        {
            MotionStreamStatus.Streaming => $"{snapshot.MotionRateHz:0} Hz · {pose.State} · decoder {(snapshot.DecoderVerified ? "verified" : "unverified")}",
            MotionStreamStatus.WaitingForFirstFrame => "Waiting for the first motion packet…",
            MotionStreamStatus.Stalled => "Motion packets stopped. Recovering…",
            MotionStreamStatus.Unsupported => "The AirPods did not stream motion.",
            _ => "Head tracking off",
        };
        RecenterButton.IsEnabled = tracking;
        DrawStage(snapshot, tracking ? pose.YawDegrees : 0f);
    }

    private void DrawStage(ServiceSnapshot snapshot, float yawDegrees)
    {
        const double centerX = 75;
        const double centerY = 90;
        const double radius = 62;
        var stage = _settings.Soundstage;
        foreach (var (dot, azimuth) in new[] { (LeftSpeakerDot, -stage.SpeakerAzimuthDegrees), (RightSpeakerDot, stage.SpeakerAzimuthDegrees) })
        {
            var radians = azimuth * Math.PI / 180;
            Canvas.SetLeft(dot, centerX + (radius * Math.Sin(radians)) - (dot.Width / 2));
            Canvas.SetTop(dot, centerY - (radius * Math.Cos(radians)) - (dot.Height / 2));
            dot.Opacity = snapshot.SpatialMode == SpatialEngineMode.Off ? 0.3 : 1;
        }

        // Yaw is positive to the LEFT (QuaternionMath convention); screen +X is right.
        var yaw = yawDegrees * Math.PI / 180;
        NoseLine.X2 = centerX - (24 * Math.Sin(yaw));
        NoseLine.Y2 = centerY - (24 * Math.Cos(yaw));
    }

    private void RenderConversation(ServiceSnapshot snapshot, bool controlsReady)
    {
        ConversationToggle.IsEnabled = controlsReady;
        ConversationStatusText.Text = !controlsReady
            ? "Needs AirPods mode control (AAP driver)."
            : snapshot.ConversationPhase switch
            {
                ConversationPhase.Disabled => "Off. When on, SSAP3 switches to Transparency while you talk.",
                ConversationPhase.Idle => "Listening for your voice (AirPods speech detection).",
                ConversationPhase.Detecting => "Speech detected…",
                ConversationPhase.InConversation => "In conversation: Transparency on.",
                ConversationPhase.Cooldown => "Conversation ended: previous mode restored.",
                ConversationPhase.SuspendedByUser => "Paused because you changed the mode manually.",
                _ => string.Empty,
            };
    }

    private async void NoiseModeRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_uiUpdating || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<NoiseControlMode>(tag, out var mode))
        {
            return;
        }

        await RequestModeAsync(mode);
    }

    private async Task RequestModeAsync(NoiseControlMode mode)
    {
        _modeRequestInFlight = true;
        SetNoiseControlsEnabled(false);
        NoiseStatusText.Text = $"Requesting {Label(mode)}…";
        try
        {
            var result = await _service.SetListeningModeAsync(mode);
            _modeResultText = result.Outcome switch
            {
                CommandOutcome.Confirmed => $"{Label(mode)} · confirmed",
                CommandOutcome.Rejected => $"Stayed in {Label(result.Reported ?? mode)}",
                CommandOutcome.Unconfirmed => "Sent, not confirmed by AirPods",
                _ => "Could not send",
            };
            StatusLog.Text = result.Message;
            Ssap3AppLog.Write($"Listening mode {mode}: {result.Outcome}.");
        }
        finally
        {
            _modeRequestInFlight = false;
            SelectNoiseMode(_service is null ? null : _snapshot?.ReportedMode);
            NoiseStatusText.Text = _modeResultText ?? string.Empty;
            SetNoiseControlsEnabled(true);
            _ = ClearModeResultLaterAsync();
        }
    }

    private async Task ClearModeResultLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        _modeResultText = null;
    }

    private async void SpatialModeRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_uiUpdating || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        var mode = tag switch
        {
            "Fixed" => SpatialEngineMode.Fixed,
            "Tracked" => SpatialEngineMode.Tracked,
            _ => SpatialEngineMode.Off,
        };
        _service.SetSpatialMode(mode);
        _settings.SpatialMode = mode;
        _settings.Save();
        if (mode != SpatialEngineMode.Off)
        {
            await DisableWindowsSonicIfEnabledAsync();
        }
    }

    private async Task DisableWindowsSonicIfEnabledAsync()
    {
        try
        {
            // SSAP3 renders binaurally itself; Windows Sonic on top would double-process.
            var status = await _windowsSpatial.GetStatusAsync();
            if (status.Enabled)
            {
                _ = await _windowsSpatial.SetWindowsSonicAsync(false);
                StatusLog.Text = "Windows Sonic turned off to avoid double processing.";
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or System.IO.IOException)
        {
            Ssap3AppLog.Write($"Windows Sonic check failed: {exception.Message}");
        }
    }

    private void RecenterButton_OnClick(object sender, RoutedEventArgs e)
    {
        _service.Recenter();
        StatusLog.Text = "Recentering: keep facing the monitor for a moment.";
    }

    private void ConversationToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_uiUpdating)
        {
            return;
        }

        var enabled = ConversationToggle.IsChecked == true;
        _service.SetConversationAwareness(enabled);
        _settings.ConversationAwarenessEnabled = enabled;
        _settings.Save();
    }

    private void ReconnectButton_OnClick(object sender, RoutedEventArgs e)
    {
        _service.Reconnect();
        if (!_scanner.IsRunning)
        {
            StartScanner();
        }

        StatusLog.Text = "Reconnecting…";
    }

    private async void ReportButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        StatusLog.Text = "Collecting redacted diagnostics…";
        try
        {
            var result = await Ssap3DiagnosticCollector.CollectAsync();
            StatusLog.Text = result.Complete ? "Diagnostic ZIP created (redacted)" : "Diagnostic ZIP created with warnings";
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{result.ZipPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            StatusLog.Text = $"Report failed: {Shorten(exception.Message, 45)}";
            Ssap3AppLog.Write($"Diagnostic collection failed: {exception}");
        }
        finally
        {
            if (sender is Button finished)
            {
                finished.IsEnabled = true;
            }
        }
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) => OpenSettings();

    internal void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }

        if (_snapshot is not null)
        {
            _settingsWindow.Render(_snapshot);
        }
    }

    internal void ApplySettings()
    {
        _settings.Save();
        _service.SetSoundstage(_settings.Soundstage);
        _service.SetCalibration(_settings.InvertYaw, _settings.InvertPitch, _settings.DriftCorrection);
        _service.SetHeadTrackingEnabled(_settings.HeadTrackingEnabled);
    }

    private void HideButton_OnClick(object sender, RoutedEventArgs e) => HideToTray();

    private void ExitButton_OnClick(object sender, RoutedEventArgs e) =>
        ((App)System.Windows.Application.Current).ExitApplication();

    private void StartScanner()
    {
        if (_scanner.Start())
        {
            return;
        }

        // Radio off or unavailable: retry quietly until Bluetooth comes back.
        _scannerRetry ??= new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) =>
        {
            if (_scanner.IsRunning || _scanner.Start())
            {
                _scannerRetry?.Stop();
            }
        }, Dispatcher);
        _scannerRetry.Start();
    }

    private DispatcherTimer? _scannerRetry;

    private void Scanner_OnAdvertisementReceived(object? sender, AirPodsAdvertisementReceivedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_advertisementFilter.Evaluate(e.Status, e.Rssi) != AdvertisementVerdict.Accepted)
            {
                return;
            }

            _advertisement = e.Status;
            _advertisementAt = DateTimeOffset.UtcNow;
            if (_snapshot is not null)
            {
                RenderBattery(_snapshot);
            }
        });
    }

    private void Scanner_OnStatusChanged(object? sender, string status)
    {
        Ssap3AppLog.Write(status);
        Dispatcher.BeginInvoke(() => StatusLog.Text = Shorten(status, 66));
    }

    private void UpdateCaseBattery(int? level, bool charging)
    {
        if (level is not null)
        {
            _settings.LastCaseBattery = level;
            _settings.LastCaseCharging = charging;
            _settings.LastCaseBatteryObservedAt = DateTimeOffset.UtcNow;
            SetBattery(CaseBatteryText, level, charging);
            CaseBatteryText.ToolTip = "Live case battery";
            return;
        }

        if (_settings.LastCaseBattery is not null &&
            _settings.LastCaseBatteryObservedAt is { } observedAt &&
            DateTimeOffset.UtcNow - observedAt < TimeSpan.FromDays(2))
        {
            CaseBatteryText.Text = $"{_settings.LastCaseBattery}%*";
            CaseBatteryText.ToolTip = $"Last seen {observedAt.LocalDateTime:g}. Open the case to refresh.";
            return;
        }

        CaseBatteryText.Text = "—";
        CaseBatteryText.ToolTip = "The case reports its battery while it is open.";
    }

    private void SelectNoiseMode(NoiseControlMode? mode)
    {
        var previous = _uiUpdating;
        _uiUpdating = true;
        NoiseOffRadio.IsChecked = mode == NoiseControlMode.Off;
        NoiseAncRadio.IsChecked = mode == NoiseControlMode.NoiseCancellation;
        NoiseTransparencyRadio.IsChecked = mode == NoiseControlMode.Transparency;
        NoiseAdaptiveRadio.IsChecked = mode == NoiseControlMode.Adaptive;
        _uiUpdating = previous;
    }

    private void SelectSpatialMode(SpatialEngineMode mode)
    {
        SpatialOffRadio.IsChecked = mode == SpatialEngineMode.Off;
        SpatialFixedRadio.IsChecked = mode == SpatialEngineMode.Fixed;
        SpatialTrackedRadio.IsChecked = mode == SpatialEngineMode.Tracked;
    }

    private void SetNoiseControlsEnabled(bool enabled)
    {
        NoiseOffRadio.IsEnabled = enabled;
        NoiseAncRadio.IsEnabled = enabled;
        NoiseTransparencyRadio.IsEnabled = enabled;
        NoiseAdaptiveRadio.IsEnabled = enabled;
    }

    private void UpdateTrayText(ServiceSnapshot snapshot)
    {
        var mode = snapshot.ReportedMode is { } reported ? $" · {Label(reported)}" : string.Empty;
        var text = $"SSAP3 · {snapshot.Status.Title}{mode}";
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private Brush SeverityBrush(StatusSeverity severity) => severity switch
    {
        StatusSeverity.Good => (Brush)FindResource("SuccessBrush"),
        StatusSeverity.Info => Brushes.Gold,
        StatusSeverity.Warning => Brushes.Orange,
        _ => Brushes.OrangeRed,
    };

    private void HideToTray()
    {
        if (_isAnimatingOut || !IsVisible)
        {
            return;
        }

        _isAnimatingOut = true;
        var generation = ++_animationGeneration;
        var fade = new DoubleAnimation(PopupRoot.Opacity, 0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
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
        PopupRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        PopupTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    private void PositionNearTray()
    {
        var cursor = WinForms.Cursor.Position;
        var screen = WinForms.Screen.FromPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = (screen.WorkingArea.Right / dpi.DpiScaleX) - Width - 8;
        Top = Math.Max(screen.WorkingArea.Top / dpi.DpiScaleY, (screen.WorkingArea.Bottom / dpi.DpiScaleY) - Height - 8);
    }

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var icon = new WinForms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "SSAP3 Control Center",
            Visible = true,
        };
        icon.MouseDown += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
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
                Dispatcher.BeginInvoke(DispatcherPriority.Send, ToggleFromTray);
            }
        };

        var menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Drawing.Color.FromArgb(32, 34, 44),
            ForeColor = Drawing.Color.White,
        };
        menu.Items.Add(MenuItem("Open SSAP3", () => ShowFromTray()));
        menu.Items.Add(MenuItem("Settings…", OpenSettings));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(MenuItem("Noise Cancellation", () => _ = RequestModeAsync(NoiseControlMode.NoiseCancellation)));
        menu.Items.Add(MenuItem("Transparency", () => _ = RequestModeAsync(NoiseControlMode.Transparency)));
        menu.Items.Add(MenuItem("Adaptive", () => _ = RequestModeAsync(NoiseControlMode.Adaptive)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(MenuItem("Recenter soundstage", () => _service.Recenter()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(MenuItem("Exit", () => ((App)System.Windows.Application.Current).ExitApplication()));
        icon.ContextMenuStrip = menu;
        return icon;
    }

    private WinForms.ToolStripMenuItem MenuItem(string text, Action action)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        item.Click += (_, _) => Dispatcher.BeginInvoke(action);
        return item;
    }

    private static Drawing.Icon CreateIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SSAP3.ico", UriKind.Absolute));
        if (resource is null)
        {
            return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        }

        using var stream = resource.Stream;
        using var icon = new Drawing.Icon(stream);
        return (Drawing.Icon)icon.Clone();
    }

    private static void SetBattery(TextBlock text, int? level, bool charging) =>
        text.Text = level is null ? "—" : $"{level}%{(charging ? " ⚡" : string.Empty)}";

    internal static string Label(NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.Off => "Off",
        NoiseControlMode.NoiseCancellation => "ANC",
        NoiseControlMode.Transparency => "Transparency",
        NoiseControlMode.Adaptive => "Adaptive",
        _ => mode.ToString(),
    };

    private static string Shorten(string value, int length) =>
        value.Length <= length ? value : $"{value[..(length - 1)]}…";
}
