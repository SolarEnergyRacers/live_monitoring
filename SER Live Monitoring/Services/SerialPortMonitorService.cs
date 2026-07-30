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

/// <summary>
/// Owns the SerialPort lifecycle and raises decoded readings for subscribers (e.g. the Home page).
/// Registered as a singleton so the connection and its listeners survive across circuits/pages.
/// </summary>
public class SerialPortMonitorService : IDisposable
{
    private readonly IDataDecoder _decoder;
    private readonly StringBuilder _buffer = new();
    private readonly Lock _bufferLock = new();
    private SerialPort? _serialPort;

    public event Action<SensorReading>? ReadingReceived;
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

        string chunk;
        try
        {
            chunk = port.ReadExisting();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Status = SerialConnectionStatus.Error;
            StatusChanged?.Invoke();
            return;
        }

        List<string> completeLines = [];
        lock (_bufferLock)
        {
            _buffer.Append(chunk);
            var text = _buffer.ToString();
            var lines = text.Split('\n');

            for (var i = 0; i < lines.Length - 1; i++)
                completeLines.Add(lines[i].TrimEnd('\r'));

            _buffer.Clear();
            _buffer.Append(lines[^1]);
        }

        foreach (var line in completeLines)
        {
            var reading = _decoder.Decode(line);
            if (reading is not null)
                ReadingReceived?.Invoke(reading);
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
