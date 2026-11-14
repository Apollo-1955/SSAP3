using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SSAP3.App.Services;
using SSAP3.Core.Spatial;
using SSAP3.Diagnostics;
using SSAP3.Platform.Windows;
using Button = System.Windows.Controls.Button;

namespace SSAP3.App;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _owner;
    private bool _loading;

    internal SettingsWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        LoadFromSettings();
        VersionText.Text = "Version " + (Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
    }

    internal void Render(ServiceSnapshot snapshot)
    {
        var pose = snapshot.Pose;
        LivePoseText.Text = pose.Calibrated
            ? $"Yaw {pose.YawDegrees,7:+0.0;-0.0}°  Pitch {pose.PitchDegrees,7:+0.0;-0.0}°  Roll {pose.RollDegrees,7:+0.0;-0.0}°"
            : "No calibrated orientation (head tracking inactive).";
        var source = snapshot.Source;
        var tracking = snapshot.Tracking;
        LiveMotionText.Text =
            $"{tracking.Summary}{(tracking.Detail is { } detail ? " · " + detail : string.Empty)}{Environment.NewLine}" +
            $"Provider: {source.Description} · {source.State} · {source.RateHz:0} Hz · sample age {(source.LastSampleAge is { } age ? $"{age.TotalMilliseconds:0} ms" : "—")} · " +
            $"received {source.SamplesReceived} · rejected {source.SamplesRejected + snapshot.RejectedSamples}";
        DecoderText.Text = source.IsSimulated
            ? "SIMULATED data: this is a development facility, not AirPods tracking."
            : $"Decoder: {snapshot.DecoderName} (NOT yet verified on AirPods Pro 3 hardware).";
        CalibrationStatusText.Text = source.Calibration switch
        {
            Core.Motion.HeadAxisCalibrationState.Collecting => "Calibrating: look up, then nod…",
            Core.Motion.HeadAxisCalibrationState.Calibrated => "Calibrated: pitch and roll are active.",
            Core.Motion.HeadAxisCalibrationState.Failed => "Calibration failed: the nod was too small or not clean. Try again with clearer up-and-down nods.",
            Core.Motion.HeadAxisCalibrationState.Rejected => "The saved calibration does not fit how the AirPods sit now. Calibrate again.",
            _ => source.ProvidesPitchRoll ? "The provider supplies pitch and roll itself." : "Not calibrated: yaw only.",
        };
        CalibrateButton.IsEnabled = tracking.Kind is Core.Motion.TrackingStatusKind.Live or Core.Motion.TrackingStatusKind.Degraded;
        DrawRoom(pose.Calibrated ? pose.YawDegrees : 0f);

        var latency = snapshot.DriverLatency.Count == 0
            ? "no samples"
            : $"median {snapshot.DriverLatency.MedianMs:0.0} ms, p95 {snapshot.DriverLatency.P95Ms:0.0} ms over {snapshot.DriverLatency.Count} packets";
        DiagnosticsText.Text = string.Join(Environment.NewLine, new[]
        {
            $"Device:            {snapshot.Status.Title}",
            $"Detail:            {snapshot.Status.Detail}",
            $"AAP driver:        {(snapshot.DriverPresent ? "present" : "not installed")}",
            $"Listening mode:    {(snapshot.ReportedMode is { } mode ? MainWindow.Label(mode) + " (reported)" : "unknown")}",
            $"Tracking:          {snapshot.Tracking.Kind} · {(snapshot.Tracking.IsRealTracking ? "REAL AirPods measurements" : snapshot.Tracking.IsSimulated ? "SIMULATED (not real tracking)" : "not tracking")}",
            $"Motion provider:   {snapshot.Source.Kind} · {snapshot.Source.Description}",
            $"Provider state:    {snapshot.Source.State}{(snapshot.Source.Problem is { } problem ? " · " + problem : string.Empty)}",
            $"Last measurement:  {(snapshot.Source.LastSampleAge is { } sampleAge ? $"{sampleAge.TotalMilliseconds:0} ms ago" : "none yet")}",
            $"Update rate:       {snapshot.Source.RateHz:0.0} Hz ({snapshot.Source.SamplesReceived} received, {snapshot.Source.SamplesRejected} rejected)",
            $"Transport delay:   {(snapshot.Source.TransportDelayMs is { } delay ? $"{delay:0.0} ms beyond the fastest packet" : "n/a")}",
            $"Calibration:       reference {(snapshot.Pose.Calibrated ? "set" : "not set")} · pitch/roll {snapshot.Source.Calibration}",
            $"Driver→app delay:  {latency} (measured)",
            $"Audio latency:     engine block size from Windows (Equalizer APO host); not measured by SSAP3",
            $"Kernel timestamps: {(MonotonicClock.UsesInterruptTime ? "available" : "unavailable")}",
            $"Spatial engine:    {(snapshot.SpatialEngineInstalled ? "installed" : "not installed")} · mode {snapshot.SpatialMode}",
            $"Engine state:      {snapshot.SpatialEngineError ?? "ok"}",
            $"Conversation:      {snapshot.ConversationPhase}",
        });
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var settings = _owner.Settings;
        var stage = settings.Soundstage;
        AzimuthSlider.Value = stage.SpeakerAzimuthDegrees;
        DistanceSlider.Value = stage.SpeakerDistanceMeters;
        ElevationSlider.Value = stage.SpeakerElevationDegrees;
        RoomSlider.Value = stage.RoomMix;
        IntensitySlider.Value = stage.Intensity;
        RendererMeasuredRadio.IsChecked = stage.Renderer == SpatialRenderer.Measured;
        RendererClassicRadio.IsChecked = stage.Renderer == SpatialRenderer.Classic;
        HeadTrackingCheck.IsChecked = settings.HeadTrackingEnabled;
        DriftCheck.IsChecked = settings.DriftCorrection;
        InvertYawCheck.IsChecked = settings.InvertYaw;
        InvertPitchCheck.IsChecked = settings.InvertPitch;
        SilenceSlider.Value = settings.ConversationSilenceSeconds;
        SourceSensorsRadio.IsChecked = settings.MotionSource == Core.Motion.MotionSourceKind.AirPodsSensors;
        SourceCompanionRadio.IsChecked = settings.MotionSource == Core.Motion.MotionSourceKind.Companion;
        SourceSimulatedRadio.IsChecked = settings.MotionSource == Core.Motion.MotionSourceKind.Simulated;
        CompanionHostBox.Text = settings.CompanionHost ?? string.Empty;
        CompanionPortBox.Text = settings.CompanionPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CompanionPanel.IsEnabled = settings.MotionSource == Core.Motion.MotionSourceKind.Companion;
        PairingStatusText.Text = CompanionPairingStore.Exists ? "Paired (the pairing code is not shown again)." : "Not paired.";
        _loading = false;
        UpdateLabels();
        DrawRoom(0);
    }

    private void StageSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyStageFromControls();

    private void Renderer_OnChanged(object sender, RoutedEventArgs e) => ApplyStageFromControls();

    private void ApplyStageFromControls()
    {
        if (_loading)
        {
            return;
        }

        _owner.Settings.Soundstage = new SoundstageSettings
        {
            SpeakerAzimuthDegrees = (float)AzimuthSlider.Value,
            SpeakerDistanceMeters = (float)DistanceSlider.Value,
            SpeakerElevationDegrees = (float)ElevationSlider.Value,
            RoomMix = (float)RoomSlider.Value,
            Intensity = (float)IntensitySlider.Value,
            Renderer = RendererClassicRadio.IsChecked == true ? SpatialRenderer.Classic : SpatialRenderer.Measured,
        }.Sanitized();
        _owner.ApplySettings();
        UpdateLabels();
        DrawRoom(_owner.LatestSnapshot?.Pose.YawDegrees ?? 0f);
    }

    private void RestoreStage_OnClick(object sender, RoutedEventArgs e)
    {
        _owner.Settings.Soundstage = SoundstageSettings.Default;
        _owner.ApplySettings();
        LoadFromSettings();
    }

    private void Calibration_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var settings = _owner.Settings;
        settings.HeadTrackingEnabled = HeadTrackingCheck.IsChecked == true;
        settings.DriftCorrection = DriftCheck.IsChecked == true;
        settings.InvertYaw = InvertYawCheck.IsChecked == true;
        settings.InvertPitch = InvertPitchCheck.IsChecked == true;
        _owner.ApplySettings();
    }

    private void SilenceSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _owner.Settings.ConversationSilenceSeconds = (int)Math.Round(SilenceSlider.Value);
        _owner.Settings.Save();
        UpdateLabels();
    }

    private void Recenter_OnClick(object sender, RoutedEventArgs e) => _owner.Service.Recenter();

    private void MotionSource_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var kind = SourceCompanionRadio.IsChecked == true ? Core.Motion.MotionSourceKind.Companion
            : SourceSimulatedRadio.IsChecked == true ? Core.Motion.MotionSourceKind.Simulated
            : Core.Motion.MotionSourceKind.AirPodsSensors;
        _owner.Settings.MotionSource = kind;
        CompanionPanel.IsEnabled = kind == Core.Motion.MotionSourceKind.Companion;
        _owner.ApplyMotionSource();
    }

    private void CompanionApply_OnClick(object sender, RoutedEventArgs e)
    {
        var host = CompanionHostBox.Text.Trim();
        if (!int.TryParse(CompanionPortBox.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            PairingStatusText.Text = "The port must be a number from 1024 to 65535.";
            return;
        }

        if (System.Net.IPAddress.TryParse(host, out var address) && !Core.Motion.Companion.CompanionProtocol.IsAllowedAddress(address))
        {
            PairingStatusText.Text = "SSAP3 only connects to companions on this PC, your local network or a private overlay (for example Tailscale).";
            return;
        }

        _owner.Settings.CompanionHost = host.Length == 0 ? null : host;
        _owner.Settings.CompanionPort = port;
        _owner.ApplyMotionSource();
        PairingStatusText.Text = CompanionPairingStore.Exists ? "Saved. Connecting…" : "Saved. Create a pairing code next.";
    }

    private void CreatePairing_OnClick(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            this,
            "Create a new pairing code? Any companion paired before stops working.\n\nAnyone with this code can send head motion to SSAP3, so enter it only in your own companion app and do not share it.",
            "Pair a companion",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        PairingCodeBox.Text = CompanionPairingStore.CreateNew();
        PairingCodeBox.Visibility = Visibility.Visible;
        PairingStatusText.Text = "Enter this code in the companion. It is stored encrypted for your Windows account and is not shown again after you close this window.";
        _owner.ApplyMotionSource();
    }

    private void Unpair_OnClick(object sender, RoutedEventArgs e)
    {
        CompanionPairingStore.Delete();
        PairingCodeBox.Text = string.Empty;
        PairingCodeBox.Visibility = Visibility.Collapsed;
        PairingStatusText.Text = "Not paired.";
        _owner.ApplyMotionSource();
    }

    private void Calibrate_OnClick(object sender, RoutedEventArgs e)
    {
        _owner.Service.BeginPitchRollCalibration();
        CalibrationStatusText.Text = "Calibrating: look up, then nod three or four times…";
    }

    private void ForgetCalibration_OnClick(object sender, RoutedEventArgs e) => _owner.ForgetPitchRollCalibration();

    private async void Report_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        try
        {
            var result = await Ssap3DiagnosticCollector.CollectAsync();
            _ = Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{result.ZipPath}\"", UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(this, exception.Message, "SSAP3 diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (sender is Button finished)
            {
                finished.IsEnabled = true;
            }
        }
    }

    private void OpenLogs_OnClick(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(Ssap3AppLog.FilePath)!;
        Directory.CreateDirectory(folder);
        _ = Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{folder}\"", UseShellExecute = true });
    }

    private void UpdateLabels()
    {
        AzimuthLabel.Text = $"Speaker angle: ±{AzimuthSlider.Value:0}°";
        DistanceLabel.Text = $"Distance: {DistanceSlider.Value:0.0} m";
        ElevationLabel.Text = $"Height: {ElevationSlider.Value:+0;-0;0}°";
        RoomLabel.Text = $"Room reflections: {RoomSlider.Value:P0}";
        IntensityLabel.Text = $"Spatial intensity: {IntensitySlider.Value:P0}";
        SilenceLabel.Text = $"Silence before restoring: {SilenceSlider.Value:0} s";
    }

    private void DrawRoom(float yawDegrees)
    {
        const double centerX = 150;
        const double centerY = 152;
        var radius = 40 + (DistanceSlider.Value / 5.0 * 90);
        var azimuth = AzimuthSlider.Value * Math.PI / 180;
        Canvas.SetLeft(RoomLeftSpeaker, centerX - (radius * Math.Sin(azimuth)) - 8);
        Canvas.SetTop(RoomLeftSpeaker, centerY - (radius * Math.Cos(azimuth)) - 8);
        Canvas.SetLeft(RoomRightSpeaker, centerX + (radius * Math.Sin(azimuth)) - 8);
        Canvas.SetTop(RoomRightSpeaker, centerY - (radius * Math.Cos(azimuth)) - 8);
        RoomOutline.Opacity = 0.3 + (0.7 * RoomSlider.Value);

        var yaw = yawDegrees * Math.PI / 180;
        RoomNose.X2 = centerX - (32 * Math.Sin(yaw));
        RoomNose.Y2 = centerY - (32 * Math.Cos(yaw));
    }
}
