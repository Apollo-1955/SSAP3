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
        LiveMotionText.Text =
            $"Stream: {snapshot.Motion} · {snapshot.MotionRateHz:0} Hz · tracking {pose.State} · sample age {pose.SampleAge.TotalMilliseconds:0} ms · " +
            $"frames {snapshot.MotionFrames} · rejected {snapshot.RejectedSamples}";
        DecoderText.Text = $"Decoder: {snapshot.DecoderName} ({(snapshot.DecoderVerified ? "hardware-verified" : "NOT hardware-verified on AirPods Pro 3")}).";
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
            $"Motion stream:     {snapshot.Motion}, {snapshot.MotionRateHz:0} Hz, {snapshot.MotionFrames} frames",
            $"Driver→app delay:  {latency} (measured)",
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
        HeadTrackingCheck.IsChecked = settings.HeadTrackingEnabled;
        DriftCheck.IsChecked = settings.DriftCorrection;
        InvertYawCheck.IsChecked = settings.InvertYaw;
        InvertPitchCheck.IsChecked = settings.InvertPitch;
        SilenceSlider.Value = settings.ConversationSilenceSeconds;
        _loading = false;
        UpdateLabels();
        DrawRoom(0);
    }

    private void StageSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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
