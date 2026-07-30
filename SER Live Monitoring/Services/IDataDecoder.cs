using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

public interface IDataDecoder
{
    SensorReading? Decode(string rawLine);
}
