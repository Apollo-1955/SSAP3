namespace SSAP3.Core;

public sealed record AirPodsAdvertisementStatus(
    string Address,
    DateTimeOffset LastSeen,
    bool IsIdentityResolved,
    bool Paired,
    string Model,
    int? LeftBattery,
    int? RightBattery,
    int? CaseBattery,
    bool IsLeftInEar,
    bool IsRightInEar,
    bool IsLeftCharging,
    bool IsRightCharging,
    bool IsCaseCharging,
    bool LidOpen,
    string Color,
    string ConnectionState,
    int ModelId = 0);
