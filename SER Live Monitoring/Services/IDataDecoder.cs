using SERLiveMonitoring.Models;

namespace SERLiveMonitoring.Services;

public interface IDataDecoder
{
    List<Reading> Decode(byte[] data);
}
