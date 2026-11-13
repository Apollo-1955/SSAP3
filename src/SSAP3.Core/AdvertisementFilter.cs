namespace SSAP3.Core;

public enum AdvertisementVerdict
{
    Accepted,
    RejectedModel,
    RejectedSignal,
}

/// <summary>
/// Decides whether a nearby Apple advertisement plausibly belongs to the user's
/// paired AirPods (SECURITY_AUDIT.md, S-12). Without an identity resolving key,
/// advertisements cannot be cryptographically tied to a device, so SSAP3:
/// <list type="bullet">
/// <item>requires the model to match the paired device's product ID when it is known;</item>
/// <item>requires a strong signal (the user's own AirPods are within arm's reach);</item>
/// <item>exposes <see cref="AirPodsAdvertisementStatus.IsIdentityResolved"/> so the UI can say "nearby" rather than "yours".</item>
/// </list>
/// </summary>
public sealed class AdvertisementFilter
{
    public AdvertisementFilter(int? pairedModelId = null, short minimumRssi = -70)
    {
        PairedModelId = pairedModelId;
        MinimumRssi = minimumRssi;
    }

    /// <summary>Advertisement model ID of the paired device, or null when it is unknown.</summary>
    public int? PairedModelId { get; set; }

    public short MinimumRssi { get; }

    public AdvertisementVerdict Evaluate(AirPodsAdvertisementStatus status, short rssi)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (PairedModelId is { } expected && status.ModelId != expected)
        {
            return AdvertisementVerdict.RejectedModel;
        }

        // A resolved identity proves ownership regardless of distance.
        if (!status.IsIdentityResolved && rssi < MinimumRssi)
        {
            return AdvertisementVerdict.RejectedSignal;
        }

        return AdvertisementVerdict.Accepted;
    }
}
