using System;
using System.Net;
using System.Net.Sockets;
using YamlDotNet.Serialization;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
public class BridgeEndPoint
{
    public string Name { get; set; }
    public string Address { get; set; }
    public int ReceivePort { get; set; }
    public int SendPort { get; set; }
}
public class BridgeRouting
{
    public string Source { get; set; }
    public List<string> Destinations { get; set; } = new List<string>();
}
public class BridgeConfig
{
    public List<BridgeEndPoint> Bridges { get; set; }
    public List<BridgeRouting> Routing { get; set; }
    public bool LogConsole { get; set; }
}
public class UdpAudioBridge : IDisposable
{
    private readonly BridgeConfig config;
    private BridgeConfig LoadConfig(string path)
    {
        var deserializer = new DeserializerBuilder().Build();
        var yaml = File.ReadAllText(path);
        return deserializer.Deserialize<BridgeConfig>(yaml);
    }

    private Dictionary<string, UdpClient> receivers = new Dictionary<string, UdpClient>();
    public UdpAudioBridge(string configPath)
    {
        config = LoadConfig(configPath);
    }
    public void Start()
    {
        foreach (var route in config.Routing)
        {
            var sourceBridge = config.Bridges.FirstOrDefault(b => b.Name == route.Source);
            var destinationEndPoints = route.Destinations
                .Select(destinationName => config.Bridges.FirstOrDefault(b => b.Name == destinationName))
                .Where(destinationBridge => destinationBridge != null)
                .Select(destinationBridge => new IPEndPoint(IPAddress.Parse(destinationBridge.Address), destinationBridge.SendPort))
                .ToList();

            if (sourceBridge != null && destinationEndPoints.Any())
            {
                StartListeningAndForwarding(sourceBridge, destinationEndPoints);
            }
        }
    }
    private void ListenAndForward(UdpClient receiver, List<IPEndPoint> sendingEndPoints)
    {
        while (true)
        {
            var endPoint = new IPEndPoint(IPAddress.Any, 0);
            byte[] receivedBytes = receiver.Receive(ref endPoint);

            if (receivedBytes.Length < 4 + 4 + 320)
            {
                Console.WriteLine($"Received an unexpected data from dvmbridge: {receivedBytes.Length}");
                continue;
            }

            // Extract srcId
            int srcId = (receivedBytes[receivedBytes.Length - 8] << 24) |
                        (receivedBytes[receivedBytes.Length - 7] << 16) |
                        (receivedBytes[receivedBytes.Length - 6] << 8) |
                        receivedBytes[receivedBytes.Length - 5];

            // Extract dstId
            int dstId = (receivedBytes[receivedBytes.Length - 4] << 24) |
                        (receivedBytes[receivedBytes.Length - 3] << 16) |
                        (receivedBytes[receivedBytes.Length - 2] << 8) |
                        receivedBytes[receivedBytes.Length - 1];
            if (config.LogConsole)
            {
                Console.WriteLine($"Received SrcId: {srcId}, DstId: {dstId}");
            }
            // TODO: Maybe do somthing else useful with the dst id? dunno

            byte[] audioPacket = new byte[4 + 320];

            Array.Copy(receivedBytes, receivedBytes.Length - 8, audioPacket, 0, 4);
            Buffer.BlockCopy(receivedBytes, 0, audioPacket, 4, 320);

            foreach (var sendingEndPoint in sendingEndPoints)
            {
                using (UdpClient forwarder = new UdpClient())
                {
                    forwarder.Send(audioPacket, audioPacket.Length, sendingEndPoint);
                }
                if (config.LogConsole)
                {
                    Console.WriteLine($"Voice traffic on bridge: {config.Bridges.First(b => b.ReceivePort == ((IPEndPoint)receiver.Client.LocalEndPoint).Port).Name} forwarding to: {config.Bridges.First(b => b.SendPort == sendingEndPoint.Port).Name}");
                }
            }
        }
    }
    private void StartListeningAndForwarding(BridgeEndPoint sourceBridge, List<IPEndPoint> destinationEndPoints)
    {
        if (!receivers.ContainsKey(sourceBridge.Name))
        {
            var receiver = new UdpClient(sourceBridge.ReceivePort);
            receivers[sourceBridge.Name] = receiver;
        }

        Task.Run(() => ListenAndForward(receivers[sourceBridge.Name], destinationEndPoints));
    }
    public void Stop()
    {
        foreach (var receiver in receivers)
        {
            receiver.Value?.Close();
        }
    }
    public void Dispose()
    {
        Stop();
        foreach (var receiver in receivers)
        {
            receiver.Value?.Dispose();
        }
    }
    public static void Main()
    {
        using (UdpAudioBridge bridge = new UdpAudioBridge("config.yml"))
        {
            Console.WriteLine("DVM Bridges:");
            foreach (var bridgelist in bridge.config.Bridges)
            {
                Console.WriteLine($"Name: {bridgelist.Name}, Address: {bridgelist.Address}, ReceivePort: {bridgelist.ReceivePort}, SendPort: {bridgelist.SendPort}");
            }

            Console.WriteLine("\nRoutes:");
            foreach (var route in bridge.config.Routing)
            {
                Console.WriteLine($"Source: {route.Source} -> Destinations: {string.Join(", ", route.Destinations)}");
            }

            bridge.Start();
            Console.WriteLine("\nBridge started. Enter to stop");
            Console.ReadLine();
            bridge.Stop();
        }
    }
}
