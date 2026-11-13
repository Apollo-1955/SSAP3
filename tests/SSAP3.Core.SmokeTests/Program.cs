using SSAP3.Core;

var environment = new AudioEnvironment(
    WindowsBuild: 19045,
    HasAirPodsStereoRender: true,
    HasAirPodsHandsFreeRender: true,
    HasAirPodsHandsFreeCapture: true,
    HasExternalMicrophone: true,
    BluetoothLeAudioConfirmed: false);
var preferences = new UserAudioPreferences(
    SpatialAudioMode.Off,
    PreferredMicrophone.Automatic,
    PreferStereo: true,
    AllowHandsFreeFallback: true);
var recommendation = AudioProfileAdvisor.Recommend(environment, preferences);
Assert(
    recommendation.Mode == AudioRouteMode.StereoWithExternalMicrophone,
    "Expected an external microphone recommendation.");
Assert(recommendation.PreservesStereo, "Stereo should be preserved.");

Assert(
    Convert.ToHexString(
        AacpPacketFactory.CreateNoiseControl(NoiseControlMode.NoiseCancellation)) ==
    "0400040009000D02000000",
    "ANC packet does not match the known AACP command.");
Assert(
    Convert.ToHexString(AacpPacketFactory.CreateAlternateStartHeadTracking()) ==
    "040004001700000010000F000873420B081010021A0501409C0000",
    "Alternate head-tracking start packet does not match the known command.");
Assert(
    Convert.ToHexString(AacpPacketFactory.CreateAlternateStopHeadTracking()) ==
    "040004001700000010000F000875420B081010021A050100000000",
    "Alternate head-tracking stop packet does not match the known command.");

var motionParser = new HeadTrackingFrameParser();
var motionPacket = new byte[70];
new byte[]
{
    0x04, 0x00, 0x04, 0x00, 0x17, 0x00,
    0x00, 0x00, 0x10, 0x00, 0x44, 0x00,
}.CopyTo(motionPacket, 0);
for (var sample = 0; sample < 10; sample++)
{
    _ = motionParser.TryParse(motionPacket, out _);
}

motionPacket[45] = 0x00;
motionPacket[46] = 0x10;
motionPacket[47] = 0x00;
motionPacket[48] = 0xF0;
Assert(
    motionParser.TryParse(motionPacket, out var motionFrame),
    "A calibrated AirPods motion packet should parse.");
Assert(
    motionFrame.Yaw > 0,
    "Synthetic motion packet should produce positive yaw.");

var parser = new AirPodsAdvertisementParser();
var advertisement = new byte[]
{
    0x07, 0x19, 0x01, 0x1B, 0x20, 0x20, 0x85, 0x19, 0x00, 0x00, 0x05,
};
Assert(
    parser.TryParse(
        "02:00:00:00:00:01",
        advertisement,
        null,
        null,
        out var parsedAdvertisement),
    "AirPods 4 ANC advertisement should parse.");
Assert(
    parsedAdvertisement?.Model == "AirPods 4 (ANC)",
    "AirPods 4 ANC model id does not match.");

var batteryParser = new AirPodsBatteryPacketParser();
var batteryPacket = new byte[]
{
    0x04, 0x00, 0x04, 0x00, 0x04, 0x00, 0x03,
    0x04, 0x01, 78, 0x02, 0x01,
    0x02, 0x01, 81, 0x01, 0x01,
    0x08, 0x01, 64, 0x02, 0x01,
};
Assert(
    batteryParser.TryParse(batteryPacket, out var batteryUpdate),
    "A valid AACP battery notification should parse.");
Assert(
    batteryUpdate is
    {
        LeftLevel: 78,
        RightLevel: 81,
        CaseLevel: 64,
        IsRightCharging: true,
    },
    "AACP battery component values do not match.");

var now = DateTimeOffset.UtcNow;
var state = ConnectionStateMachine.Initial(now);
state = ConnectionStateMachine.Apply(state, ConnectionEvent.Start, now);
state = ConnectionStateMachine.Apply(state, ConnectionEvent.DeviceSeen, now);
state = ConnectionStateMachine.Apply(state, ConnectionEvent.AudioConnected, now);
state = ConnectionStateMachine.Apply(state, ConnectionEvent.ControlConnected, now);
Assert(state.State == ConnectionState.Ready, "Expected ready connection state.");

Console.WriteLine("SSAP3.Core smoke tests passed.");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
