using System;
using System.Net;
using System.Net.Sockets;
using YamlDotNet.Serialization;
using System.IO;
using System.Threading.Tasks;

public class BridgeEndPoint
{
    public string Address { get; set; }
    public int ReceivePort { get; set; }
    public int SendPort { get; set; }
}

public class BridgeConfig
{
    public BridgeEndPoint Bridge1 { get; set; }
    public BridgeEndPoint Bridge2 { get; set; }
}

public class UdpAudioBridge : IDisposable
{
    private UdpClient bridge1Receiver;
    private UdpClient bridge2Receiver;
    private readonly IPEndPoint bridge1SendingEndPoint;
    private readonly IPEndPoint bridge2SendingEndPoint;
    private readonly BridgeConfig config;

    private BridgeConfig LoadConfig(string path)
    {
        var deserializer = new DeserializerBuilder().Build();
        var yaml = File.ReadAllText(path);
        return deserializer.Deserialize<BridgeConfig>(yaml);
    }

    public UdpAudioBridge(string configPath)
    {
        config = LoadConfig(configPath);
        bridge1SendingEndPoint = new IPEndPoint(IPAddress.Parse(config.Bridge1.Address), config.Bridge1.SendPort);
        bridge2SendingEndPoint = new IPEndPoint(IPAddress.Parse(config.Bridge2.Address), config.Bridge2.SendPort);
    }

    public void Start()
    {
        bridge1Receiver = new UdpClient(config.Bridge1.ReceivePort);
        bridge2Receiver = new UdpClient(config.Bridge2.ReceivePort);

        Task.Run(() => ListenAndForward(bridge1Receiver, bridge2Receiver, bridge2SendingEndPoint));
        Task.Run(() => ListenAndForward(bridge2Receiver, bridge1Receiver, bridge1SendingEndPoint));
    }

    private void ListenAndForward(UdpClient receiver, UdpClient forwarder, IPEndPoint sendingEndPoint)
    {
        while (true)
        {
            var endPoint = new IPEndPoint(IPAddress.Any, 0);
            byte[] receivedBytes = receiver.Receive(ref endPoint);

            if (receivedBytes.Length < 4 + 4 + 320)
            {
                Console.WriteLine($"Received an unexpected packet size: {receivedBytes.Length}");
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

            Console.WriteLine($"Received SrcId: {srcId}, DstId: {dstId}");

            byte[] audioPacket = new byte[4 + 320];

            Array.Copy(receivedBytes, receivedBytes.Length - 8, audioPacket, 0, 4);

            Buffer.BlockCopy(receivedBytes, 0, audioPacket, 4, 320);

            forwarder.Send(audioPacket, audioPacket.Length, sendingEndPoint);
        }
    }

    public void Stop()
    {
        bridge1Receiver?.Close();
        bridge2Receiver?.Close();
    }

    public void Dispose()
    {
        Stop();
        bridge1Receiver?.Dispose();
        bridge2Receiver?.Dispose();
    }
    public static void Main()
    {
        using (UdpAudioBridge bridge = new UdpAudioBridge("config.yml"))
        {
            bridge.Start();
            Console.WriteLine("Bridge started. Press Enter to stop...");
            Console.ReadLine();
            bridge.Stop();
        }
    }
}
