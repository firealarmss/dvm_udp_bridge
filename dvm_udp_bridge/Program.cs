using System;
using System.Net;
using System.Net.Sockets;
using YamlDotNet.Serialization;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class BridgeEndPoint
{
    public string Name { get; set; }
    public string Address { get; set; }
    public int ReceivePort { get; set; }
    public int SendPort { get; set; }

    public string Type { get; set; }
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
    private void ListenAndForward(UdpClient receiver, List<IPEndPoint> sendingEndPoints, BridgeEndPoint sourceBridge)
    {
        while (true)
        {
            var endPoint = new IPEndPoint(IPAddress.Any, 0);
            byte[] receivedBytes = receiver.Receive(ref endPoint);

         //   Console.WriteLine(receivedBytes.Length);
            if (receivedBytes.Length <= 320)
            {
                Console.WriteLine($"Low bytes: {receivedBytes.Length}");
            }

            // Extract srcId and dstId
            int srcId = (receivedBytes[receivedBytes.Length - 8] << 24) |
                        (receivedBytes[receivedBytes.Length - 7] << 16) |
                        (receivedBytes[receivedBytes.Length - 6] << 8) |
                        receivedBytes[receivedBytes.Length - 5];

            int dstId = (receivedBytes[receivedBytes.Length - 4] << 24) |
                        (receivedBytes[receivedBytes.Length - 3] << 16) |
                        (receivedBytes[receivedBytes.Length - 2] << 8) |
                        receivedBytes[receivedBytes.Length - 1];

            foreach (var sendingEndPoint in sendingEndPoints)
            {
                var destinationBridge = config.Bridges.FirstOrDefault(b => b.SendPort == sendingEndPoint.Port);

                byte[] audioPacket;

                if (sourceBridge.Type == "none")
                {
                    audioPacket = receivedBytes.Take(320).ToArray();
                    srcId = 0;
                    dstId = 0;
                }
                else if (sourceBridge.Type == "client")
                {
                    dstId = 0;
                    audioPacket = new byte[4 + 320];
                    Array.Copy(receivedBytes, receivedBytes.Length - 8, audioPacket, 0, 4);
                    Buffer.BlockCopy(receivedBytes, 0, audioPacket, 4, 320);
                }
                else if (sourceBridge.Type == "dvmbridge")
                {
                    byte[] srcIdBytes = new byte[4];
                    byte[] dstIdBytes = new byte[4];
                    audioPacket = new byte[320];

                    Buffer.BlockCopy(receivedBytes, 0, audioPacket, 0, 320);
                    Array.Copy(receivedBytes, 320, srcIdBytes, 0, 4);
                    Array.Copy(receivedBytes, 324, dstIdBytes, 0, 4);
                }
                else if (sourceBridge.Type == "allstar")
                {
                    // It seems most of this stuff is plain ignored in allstar. Dumb so just ignore all the header stuff.
                    audioPacket = receivedBytes.Skip(32).Take(320).ToArray();
                    srcId = 0;
                    dstId = 0;
                }
                else
                {
                    audioPacket = new byte[4 + 320];
                    Array.Copy(receivedBytes, receivedBytes.Length - 8, audioPacket, 0, 4);
                    Buffer.BlockCopy(receivedBytes, 0, audioPacket, 4, 320);
                }
                Console.WriteLine(receivedBytes.Length);

                if (destinationBridge != null && receivedBytes.Length > 320)
                {
                    if (destinationBridge.Type == "client")
                    {
                        int originalLength = audioPacket.Length;
                        byte[] newAudioPacket = new byte[originalLength + 8];
                        byte[] srcIdBytes = BitConverter.GetBytes(srcId);
                        byte[] dstIdBytes = BitConverter.GetBytes(dstId);

                        if (BitConverter.IsLittleEndian)
                        {
                            srcIdBytes = srcIdBytes.Reverse().ToArray();
                            dstIdBytes = dstIdBytes.Reverse().ToArray();
                        }

                        Buffer.BlockCopy(srcIdBytes, 0, newAudioPacket, 0, 4);
                        Buffer.BlockCopy(dstIdBytes, 0, newAudioPacket, 4, 4);
                        Buffer.BlockCopy(audioPacket, 0, newAudioPacket, 8, originalLength);

                        audioPacket = newAudioPacket;
                        Console.WriteLine($"Send to CLIENTTTTT {destinationBridge.Name}");
                    }
                    else if (destinationBridge.Type == "dvmbridge")
                    {
                        byte[] newAudioPacket = new byte[324];

                        byte[] srcIdBytes = BitConverter.GetBytes(srcId);
                        if (BitConverter.IsLittleEndian)
                        {
                            srcIdBytes = srcIdBytes.Reverse().ToArray();
                        }

                        Buffer.BlockCopy(srcIdBytes, 0, newAudioPacket, 0, 4);
                        Buffer.BlockCopy(audioPacket, 0, newAudioPacket, 4, 320);

                        audioPacket = newAudioPacket;
                        Console.WriteLine("BRIDGEE");
                    }
                    else if(destinationBridge.Type == "allstar")
                    {
                        const uint VOICE_PACKET_TYPE = 0;
                        const uint SEQUENCE_NUMBER = 1234;

                        byte[] allstarPacket = new byte[320 + 32];
 
                        // It seems most of this stuff is plain ignored in allstar. Dumb

                        Buffer.BlockCopy(Encoding.ASCII.GetBytes("USRP"), 0, allstarPacket, 0, 4);
    
                        Buffer.BlockCopy(BitConverter.GetBytes((uint)SEQUENCE_NUMBER), 0, allstarPacket, 4, 4);
    
                        Buffer.BlockCopy(BitConverter.GetBytes((uint)2), 0, allstarPacket, 8, 4);
    
                        Buffer.BlockCopy(BitConverter.GetBytes((uint)7), 0, allstarPacket, 12, 4);
    
                        Buffer.BlockCopy(BitConverter.GetBytes((uint)dstId), 0, allstarPacket, 16, 4);
    
                        Buffer.BlockCopy(BitConverter.GetBytes(VOICE_PACKET_TYPE), 0, allstarPacket, 20, 4);
    
                        Buffer.BlockCopy(new byte[8], 0, allstarPacket, 24, 8);
    
                        Buffer.BlockCopy(audioPacket, 0, allstarPacket, 32, 320);

                        audioPacket = allstarPacket;
                    }
                }

                using (UdpClient forwarder = new UdpClient())
                {
                    forwarder.Send(audioPacket, audioPacket.Length, sendingEndPoint);
                }

                if (config.LogConsole)
                {
                    Console.WriteLine($"Voice traffic on {sourceBridge.Type} : {config.Bridges.First(b => b.ReceivePort == ((IPEndPoint)receiver.Client.LocalEndPoint).Port).Name} forwarding to: {destinationBridge?.Name}");
                    Console.WriteLine($"Received SrcId: {srcId}, DstId: {dstId}");
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

        Task.Run(() => ListenAndForward(receivers[sourceBridge.Name], destinationEndPoints, sourceBridge));
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