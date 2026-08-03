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

    public void Connect(string portName, int baudRate = 9600)
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

        try
        {
            var count = port.BytesToRead;
            chunk = new byte[count];
            port.Read(chunk, 0, count);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = SerialConnectionStatus.Error;
            StatusChanged?.Invoke();
            return;
        }

        const int PacketSize = 12;

        List<byte[]> packets = [];

        lock (_bufferLock)
        {
            _buffer.AddRange(chunk);

            while (_buffer.Count >= PacketSize)
            {
                packets.Add(_buffer.GetRange(0, PacketSize).ToArray());
                _buffer.RemoveRange(0, PacketSize);
            }
        }

        foreach (var packet in packets)
        {
            var readings = _decoder.Decode(packet);
            if (readings.Count > 0)
                ReadingReceived?.Invoke(readings);
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
