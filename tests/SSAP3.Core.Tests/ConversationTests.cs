using SSAP3.Core;
using SSAP3.Core.Conversation;

namespace SSAP3.Core.Tests;

public class ConversationModeControllerTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(50);

    private static ConversationModeController CreateInAnc()
    {
        var controller = new ConversationModeController { Enabled = true };
        controller.OnModeReported(NoiseControlMode.NoiseCancellation, TimeSpan.Zero);
        return controller;
    }

    private static (TimeSpan End, List<NoiseControlMode> Requests) Drive(
        ConversationModeController controller,
        TimeSpan start,
        TimeSpan duration,
        bool speaking)
    {
        var requests = new List<NoiseControlMode>();
        for (var t = start; t < start + duration; t += Frame)
        {
            var decision = controller.OnSpeech(speaking, t);
            if (decision.RequestedMode is { } mode)
            {
                requests.Add(mode);
                controller.OnModeReported(mode, t);
            }
        }

        return (start + duration, requests);
    }

    [Fact]
    public void Speech_in_anc_requests_transparency_then_restores_anc()
    {
        var controller = CreateInAnc();
        var (t, requests) = Drive(controller, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), speaking: true);
        Assert.Equal([NoiseControlMode.Transparency], requests);
        Assert.Equal(ConversationPhase.InConversation, controller.Phase);

        (t, requests) = Drive(controller, t, TimeSpan.FromSeconds(7), speaking: false);
        Assert.Equal([NoiseControlMode.NoiseCancellation], requests);
        Assert.Equal(ConversationPhase.Cooldown, controller.Phase);
    }

    [Fact]
    public void Brief_pauses_do_not_flip_modes()
    {
        var controller = CreateInAnc();
        var (t, requests) = Drive(controller, TimeSpan.Zero, TimeSpan.FromSeconds(1), speaking: true);
        for (var sentence = 0; sentence < 10; sentence++)
        {
            var (afterPause, pauseRequests) = Drive(controller, t, TimeSpan.FromSeconds(2), speaking: false);
            var (afterSpeech, speechRequests) = Drive(controller, afterPause, TimeSpan.FromSeconds(1), speaking: true);
            requests.AddRange(pauseRequests);
            requests.AddRange(speechRequests);
            t = afterSpeech;
        }

        Assert.Equal([NoiseControlMode.Transparency], requests);
    }

    [Fact]
    public void A_cough_shorter_than_the_attack_time_is_ignored()
    {
        var controller = CreateInAnc();
        var (t, requests) = Drive(controller, TimeSpan.Zero, TimeSpan.FromMilliseconds(200), speaking: true);
        (_, var later) = Drive(controller, t, TimeSpan.FromSeconds(2), speaking: false);
        Assert.Empty(requests);
        Assert.Empty(later);
        Assert.Equal(ConversationPhase.Idle, controller.Phase);
    }

    [Fact]
    public void Minimum_conversation_time_is_respected()
    {
        var controller = new ConversationModeController(new ConversationModeOptions
        {
            SilenceTimeout = TimeSpan.FromSeconds(1),
            MinimumConversation = TimeSpan.FromSeconds(5),
        })
        { Enabled = true };
        controller.OnModeReported(NoiseControlMode.NoiseCancellation, TimeSpan.Zero);
        var (t, _) = Drive(controller, TimeSpan.Zero, TimeSpan.FromSeconds(0.5), speaking: true);
        var (_, early) = Drive(controller, t, TimeSpan.FromSeconds(3), speaking: false);
        Assert.Empty(early);
        var (_, late) = Drive(controller, t + TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), speaking: false);
        Assert.Equal([NoiseControlMode.NoiseCancellation], late);
    }

    [Fact]
    public void Manual_mode_change_suspends_automation()
    {
        var controller = CreateInAnc();
        var (t, _) = Drive(controller, TimeSpan.Zero, TimeSpan.FromSeconds(1), speaking: true);
        controller.OnModeReported(NoiseControlMode.Adaptive, t);
        Assert.Equal(ConversationPhase.SuspendedByUser, controller.Phase);

        var (_, requests) = Drive(controller, t, TimeSpan.FromSeconds(20), speaking: true);
        Assert.Empty(requests);
    }

    [Fact]
    public void Does_nothing_when_disabled_or_already_in_transparency()
    {
        var disabled = new ConversationModeController();
        disabled.OnModeReported(NoiseControlMode.NoiseCancellation, TimeSpan.Zero);
        Assert.Empty(Drive(disabled, TimeSpan.Zero, TimeSpan.FromSeconds(3), speaking: true).Requests);

        var transparent = new ConversationModeController { Enabled = true };
        transparent.OnModeReported(NoiseControlMode.Transparency, TimeSpan.Zero);
        Assert.Empty(Drive(transparent, TimeSpan.Zero, TimeSpan.FromSeconds(3), speaking: true).Requests);
    }

    [Fact]
    public void Failed_requests_back_off_instead_of_retrying_immediately()
    {
        var controller = CreateInAnc();
        var t = TimeSpan.Zero;
        ConversationDecision decision = default;
        for (; decision.RequestedMode is null; t += Frame)
        {
            decision = controller.OnSpeech(true, t);
        }

        controller.OnRequestFailed(t);
        Assert.Equal(ConversationPhase.Cooldown, controller.Phase);
        Assert.Null(controller.OnSpeech(true, t + Frame).RequestedMode);
    }
}

public class VoiceActivityDetectorTests
{
    private const int Rate = 16000;
    private const int FrameLength = 320;

    private static float[] Noise(Random random, float amplitude) =>
        Enumerable.Range(0, FrameLength).Select(_ => (float)((random.NextDouble() - 0.5) * 2 * amplitude)).ToArray();

    private static float[] Voiced(int frameIndex, float amplitude)
    {
        var frame = new float[FrameLength];
        for (var n = 0; n < FrameLength; n++)
        {
            var t = ((frameIndex * FrameLength) + n) / (double)Rate;
            var voice = Math.Sin(2 * Math.PI * 140 * t) + (0.6 * Math.Sin(2 * Math.PI * 280 * t)) + (0.3 * Math.Sin(2 * Math.PI * 420 * t));
            frame[n] = (float)(amplitude * voice / 1.9);
        }

        return frame;
    }

    [Fact]
    public void Steady_room_noise_is_not_speech()
    {
        var random = new Random(7);
        var vad = new VoiceActivityDetector(Rate);
        var detections = Enumerable.Range(0, 300).Count(_ => vad.ProcessFrame(Noise(random, 0.01f)));
        Assert.True(detections < 5, $"{detections} noise frames were classified as speech.");
    }

    [Fact]
    public void Voiced_speech_over_noise_is_detected()
    {
        var random = new Random(9);
        var vad = new VoiceActivityDetector(Rate);
        for (var index = 0; index < 100; index++)
        {
            vad.ProcessFrame(Noise(random, 0.005f));
        }

        var detected = 0;
        for (var index = 0; index < 50; index++)
        {
            var frame = Voiced(index, 0.2f).Zip(Noise(random, 0.005f), (a, b) => a + b).ToArray();
            detected += vad.ProcessFrame(frame) ? 1 : 0;
        }

        Assert.True(detected > 40, $"Only {detected} of 50 speech frames were detected.");
    }

    [Fact]
    public void Non_finite_samples_are_safe()
    {
        var vad = new VoiceActivityDetector(Rate);
        var frame = new float[FrameLength];
        Array.Fill(frame, float.NaN);
        frame[3] = float.PositiveInfinity;
        Assert.False(vad.ProcessFrame(frame));
        Assert.True(double.IsFinite(vad.NoiseFloorDb));
    }

    [Fact]
    public void Rejects_unreasonable_frame_sizes()
    {
        var vad = new VoiceActivityDetector(Rate);
        Assert.Throws<ArgumentException>(() => vad.ProcessFrame(new float[10]));
        Assert.Throws<ArgumentException>(() => vad.ProcessFrame(new float[Rate]));
    }
}
