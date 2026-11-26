using System.Collections.Concurrent;
using System.Text.Json;

namespace MediDispenserWeb.Services;

/// <summary>Manages Server-Sent Events connections for real-time browser updates.</summary>
public class SseManager
{
    private readonly ConcurrentDictionary<string, StreamWriter> _clients = new();
    private readonly ILogger<SseManager> _log;

    public SseManager(ILogger<SseManager> log)
    {
        _log = log;
    }

    public string AddClient(StreamWriter writer)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _clients[id] = writer;
        return id;
    }

    public void RemoveClient(string id)
    {
        _clients.TryRemove(id, out _);
    }

    public void Broadcast(string eventType, string data)
    {
        var deadClients = new List<string>();
        foreach (var (id, writer) in _clients)
        {
            try
            {
                writer.Write($"event: {eventType}\ndata: {data}\n\n");
                writer.Flush();
            }
            catch
            {
                deadClients.Add(id);
            }
        }
        foreach (var id in deadClients)
        {
            _clients.TryRemove(id, out _);
        }
    }
}
