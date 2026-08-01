using SER_Live_Monitoring.Models;

namespace SER_Live_Monitoring.Services;

public interface IReadingCache
{
    event Action<Reading>? ReadingAdded;

    Reading? Latest { get; }

    IReadOnlyList<Reading> GetHistory();

    void Clear();
}
