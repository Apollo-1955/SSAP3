using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SSAP3.Core.Motion;
using SSAP3.Core.Motion.Companion;
using SSAP3.Platform.Windows;

namespace SSAP3.TransportProbe;

/// <summary>
/// Reference companion for development: serves SIMULATED head motion over the
/// SSAP3 companion protocol on 127.0.0.1 only, so the companion path can be
/// tested end to end on one PC. Every sample carries the Simulated flag, and
/// SSAP3 shows it as simulated. Companion implementers can use it as a
/// working reference (docs/COMPANION_PROTOCOL.md).
/// </summary>
internal static class CompanionSimulatorServer
{
    public static int Run(int port, int seconds)
    {
        var key = CompanionPairingStore.Load();
        if (key is null)
        {
            Console.WriteLine("No pairing key: in SSAP3, open Settings > Head tracking, choose Companion and create a pairing code first.");
            return 2;
        }

        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"Companion simulator listening on 127.0.0.1:{port} for {seconds} s. In SSAP3 set the companion address to 127.0.0.1 and port {port}.");
        Console.WriteLine("All data is SIMULATED and labelled as such.");
        var deadline = Stopwatch.StartNew();
        try
        {
            while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                if (!listener.Pending())
                {
                    Thread.Sleep(50);
                    continue;
                }

                using var client = listener.AcceptTcpClient();
                client.NoDelay = true;
                try
                {
                    Serve(client.GetStream(), key, deadline, seconds);
                }
                catch (Exception exception) when (exception is IOException or SocketException)
                {
                    Console.WriteLine($"Session ended: {exception.Message}");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return 0;
    }

    private static void Serve(NetworkStream stream, byte[] key, Stopwatch deadline, int seconds)
    {
        stream.ReadTimeout = 3000;
        var hello = new byte[CompanionProtocol.ClientHelloLength];
        stream.ReadExactly(hello);
        var clientNonce = new byte[CompanionProtocol.NonceLength];
        if (!CompanionProtocol.TryParseClientHello(hello, clientNonce))
        {
            Console.WriteLine("Rejected: not an SSAP3 client hello.");
            return;
        }

        var serverNonce = CompanionProtocol.CreateNonce();
        stream.Write(CompanionProtocol.CreateServerHello(key, clientNonce, serverNonce));
        var proof = new byte[CompanionProtocol.ProofLength];
        stream.ReadExactly(proof);
        if (!CompanionProtocol.VerifyClientProof(key, clientNonce, serverNonce, proof))
        {
            Console.WriteLine("Rejected: SSAP3 did not prove the pairing key.");
            return;
        }

        var sessionKey = CompanionProtocol.DeriveSessionKey(key, clientNonce, serverNonce);
        using var aes = new AesGcm(sessionKey, 16);
        CryptographicOperations.ZeroMemory(sessionKey);
        Console.WriteLine("SSAP3 connected and authenticated; streaming simulated motion at 50 Hz.");

        var simulation = new SimulatedMotionSource(new SimulatedMotionOptions { Pattern = SimulatedHeadMotion.Natural, DropoutEvery = TimeSpan.Zero, StallEvery = TimeSpan.Zero });
        var clock = Stopwatch.StartNew();
        simulation.Start(TimeSpan.Zero);
        var samples = new List<OrientationSample>();
        ulong counter = 0;
        while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            samples.Clear();
            simulation.Poll(clock.Elapsed, samples);
            foreach (var sample in samples)
            {
                var record = new CompanionSample(
                    CompanionSampleKind.HeadOrientation,
                    CompanionSampleFlags.Simulated,
                    (ulong)(sample.Timestamp.Ticks / 10),
                    sample.Orientation,
                    0f);
                stream.Write(CompanionProtocol.SealRecord(aes, ++counter, record));
            }

            Thread.Sleep(10);
        }
    }
}
