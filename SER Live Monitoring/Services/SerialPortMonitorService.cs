using System.IO.Ports;
using System.Text;
using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

public enum SerialConnectionStatus
{
    Disconnected,
    Connected,
    Error
}

public class SerialPortMonitorService : IDisposable
{
    // Packet layout: [sync+addrHi][addrLo][8 data bytes][0x0A]. The sync bytes top
    // 5 bits are always 1, its low 3 bits are the address MSBs, followed by a full
    // address byte, 8 data bytes, then a 0x0A terminator.
    private const int PacketSize = 11;
    private const byte SyncMask = 0b1111_1000;
    private const byte Terminator = 0x0A;

    private readonly IDataDecoder _decoder;
    private readonly List<byte> _buffer = new();
    private readonly Lock _bufferLock = new();
    private SerialPort? _serialPort;

    public event Action<List<Reading>>? ReadingReceived;
    public event Action? StatusChanged;

    public SerialConnectionStatus Status { get; private set; } = SerialConnectionStatus.Disconnected;
    public string? PortName { get; private set; }
    public string? LastError { get; private set; }

    public SerialPortMonitorService(IDataDecoder decoder)
    {
        _decoder = decoder;
    }

    public static string[] GetAvailablePortNames() => SerialPort.GetPortNames();

    public void Connect(string portName, int baudRate = 115200)
    {
        Disconnect();

        try
        {
            _serialPort = new SerialPort(portName, baudRate)
            {
                NewLine = "\n"
            };
            _serialPort.DataReceived += OnDataReceived;
            _serialPort.Open();

            PortName = portName;
            LastError = null;
            Status = SerialConnectionStatus.Connected;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = SerialConnectionStatus.Error;
            _serialPort?.Dispose();
            _serialPort = null;
        }

        StatusChanged?.Invoke();
    }

    public void Disconnect()
    {
        if (_serialPort is null)
            return;

        _serialPort.DataReceived -= OnDataReceived;
        try
        {
            if (_serialPort.IsOpen)
                _serialPort.Close();
        }
        catch
        {
            // ignore errors while tearing down the port
        }
        _serialPort.Dispose();
        _serialPort = null;

        PortName = null;
        Status = SerialConnectionStatus.Disconnected;
        StatusChanged?.Invoke();
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort is not { IsOpen: true } port)
            return;

        byte[] chunk;
        int bytesRead;

        try
        {
            var count = port.BytesToRead;
            chunk = new byte[count];
            bytesRead = port.Read(chunk, 0, count);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = SerialConnectionStatus.Error;
            StatusChanged?.Invoke();
            return;
        }

        List<byte[]> packets;

        lock (_bufferLock)
        {
            _buffer.AddRange(chunk.AsSpan(0, bytesRead));
            packets = ExtractPackets(_buffer);
        }

        foreach (var packet in packets)
        {
            var readings = _decoder.Decode(packet);
            if (readings.Count > 0)
                ReadingReceived?.Invoke(readings);
        }
    }

    // Pulls as many complete packets out of the front of buffer as possible
    private static List<byte[]> ExtractPackets(List<byte> buffer)
    {
        var packets = new List<byte[]>();

        while (true)
        {
            while (buffer.Count > 0 && (buffer[0] & SyncMask) != SyncMask)
                buffer.RemoveAt(0);

            if (buffer.Count < PacketSize)
                break;

            if (buffer[PacketSize - 1] == Terminator)
            {
                packets.Add(buffer.GetRange(0, PacketSize - 1).ToArray());
                buffer.RemoveRange(0, PacketSize);
            }
            else
            {
                buffer.RemoveAt(0);
            }
        }

        return packets;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
