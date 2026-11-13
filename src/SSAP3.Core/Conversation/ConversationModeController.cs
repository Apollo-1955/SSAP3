namespace SSAP3.Core.Conversation;

public enum ConversationPhase
{
    Disabled,
    Idle,
    Detecting,
    InConversation,
    Cooldown,
    SuspendedByUser,
}

public enum SpeechSource
{
    /// <summary>The AirPods' own speaking-level notifications (AACP 0x4B).</summary>
    AirPods,

    /// <summary>SSAP3's local voice-activity detector on a non-AirPods microphone.</summary>
    ComputerMicrophone,
}

public sealed record ConversationModeOptions
{
    /// <summary>Continuous speech required before switching to Transparency.</summary>
    public TimeSpan SpeechAttack { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Gap tolerated inside "continuous" speech while detecting (pauses between syllables).</summary>
    public TimeSpan AttackGapTolerance { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Silence after the last speech before the previous mode is restored.</summary>
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Minimum time in Transparency, so brief pauses never flip modes back and forth.</summary>
    public TimeSpan MinimumConversation { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>After restoring, speech is ignored for this long.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long automation stays paused after the user changes mode manually.</summary>
    public TimeSpan ManualOverrideHold { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>A mode change the controller wants; the caller must apply it and report the outcome.</summary>
public readonly record struct ConversationDecision(NoiseControlMode? RequestedMode, string Reason)
{
    public static ConversationDecision None { get; } = new(null, string.Empty);
}

/// <summary>
/// SSAP3's local conversation-aware mode switching (not Apple's Conversation
/// Awareness). While the AirPods are in a non-Transparency mode and the user
/// starts speaking, it requests Transparency. After a silence timeout it
/// restores the previous mode. Attack time, minimum dwell and cooldown
/// provide hysteresis. A manual mode change pauses automation. The
/// controller performs no I/O.
/// </summary>
public sealed class ConversationModeController
{
    private readonly ConversationModeOptions _options;
    private TimeSpan _speechStartedAt;
    private TimeSpan _lastSpeechAt;
    private TimeSpan _conversationStartedAt;
    private TimeSpan _phaseUntil;
    private NoiseControlMode? _expectedMode;

    public ConversationModeController(ConversationModeOptions? options = null)
    {
        _options = options ?? new ConversationModeOptions();
    }

    public ConversationPhase Phase { get; private set; } = ConversationPhase.Disabled;

    /// <summary>Mode the AirPods last confirmed (from <see cref="OnModeReported"/>).</summary>
    public NoiseControlMode? CurrentMode { get; private set; }

    /// <summary>Mode to restore when the conversation ends.</summary>
    public NoiseControlMode? RestoreMode { get; private set; }

    public bool Enabled
    {
        get => Phase != ConversationPhase.Disabled;
        set
        {
            if (value && Phase == ConversationPhase.Disabled)
            {
                Phase = ConversationPhase.Idle;
            }
            else if (!value)
            {
                Phase = ConversationPhase.Disabled;
                RestoreMode = null;
                _expectedMode = null;
            }
        }
    }

    /// <summary>Feed every confirmed mode report. Reports SSAP3 did not request count as a manual override.</summary>
    public void OnModeReported(NoiseControlMode mode, TimeSpan now)
    {
        var expected = _expectedMode;
        CurrentMode = mode;
        if (expected == mode)
        {
            _expectedMode = null;
            return;
        }

        if (Phase is ConversationPhase.Detecting or ConversationPhase.InConversation or ConversationPhase.Cooldown)
        {
            Phase = ConversationPhase.SuspendedByUser;
            _phaseUntil = now + _options.ManualOverrideHold;
            RestoreMode = null;
            _expectedMode = null;
        }
    }

    /// <summary>The caller could not apply a requested change; stop and do not retry immediately.</summary>
    public void OnRequestFailed(TimeSpan now)
    {
        _expectedMode = null;
        RestoreMode = null;
        if (Phase != ConversationPhase.Disabled)
        {
            Phase = ConversationPhase.Cooldown;
            _phaseUntil = now + _options.Cooldown;
        }
    }

    public ConversationDecision OnSpeech(bool speaking, TimeSpan now)
    {
        if (speaking)
        {
            _lastSpeechAt = now;
        }

        switch (Phase)
        {
            case ConversationPhase.Idle when speaking && CanStartConversation():
                Phase = ConversationPhase.Detecting;
                _speechStartedAt = now;
                return ConversationDecision.None;
            case ConversationPhase.Detecting when speaking && now - _speechStartedAt >= _options.SpeechAttack:
                RestoreMode = CurrentMode;
                _expectedMode = NoiseControlMode.Transparency;
                _conversationStartedAt = now;
                Phase = ConversationPhase.InConversation;
                return new ConversationDecision(NoiseControlMode.Transparency, "Speech detected.");
            default:
                return Tick(now);
        }
    }

    public ConversationDecision Tick(TimeSpan now)
    {
        switch (Phase)
        {
            case ConversationPhase.Detecting when now - _lastSpeechAt > _options.AttackGapTolerance:
                Phase = ConversationPhase.Idle;
                return ConversationDecision.None;
            case ConversationPhase.InConversation
                when now - _lastSpeechAt >= _options.SilenceTimeout &&
                     now - _conversationStartedAt >= _options.MinimumConversation:
            {
                var restore = RestoreMode;
                RestoreMode = null;
                Phase = ConversationPhase.Cooldown;
                _phaseUntil = now + _options.Cooldown;
                if (restore is null || restore == NoiseControlMode.Transparency)
                {
                    return ConversationDecision.None;
                }

                _expectedMode = restore;
                return new ConversationDecision(restore, "Conversation ended.");
            }

            case ConversationPhase.Cooldown or ConversationPhase.SuspendedByUser when now >= _phaseUntil:
                Phase = ConversationPhase.Idle;
                return ConversationDecision.None;
            default:
                return ConversationDecision.None;
        }
    }

    private bool CanStartConversation() =>
        CurrentMode is NoiseControlMode.NoiseCancellation or NoiseControlMode.Adaptive or NoiseControlMode.Off;
}
