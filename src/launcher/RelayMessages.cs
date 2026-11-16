using System.Net.WebSockets;

#if RENDEZVOUS_SERVER
namespace TH12Coop.Rendezvous;
#else
namespace TH12Coop;
#endif

// One reader per direction. The returned bytes remain valid until the next read.
internal sealed class RelayMessages
{
    internal const int MaxBytes = 16 * 1024;
    // One extra byte distinguishes an oversized message from an exactly-full
    // fragment followed by an empty final frame.
    private readonly byte[] buffer = new byte[MaxBytes + 1];

    internal async ValueTask<ReadOnlyMemory<byte>> Read(WebSocket socket, CancellationToken token)
    {
        int size = 0;
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer.AsMemory(size), token).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close && size == 0) return ReadOnlyMemory<byte>.Empty;
            size += part.Count;
            if (part.MessageType != WebSocketMessageType.Binary || size > MaxBytes ||
                part.Count == 0 && (!part.EndOfMessage || size == 0))
            {
                socket.Abort();
                throw new IOException("中継メッセージの形式または長さが不正です。");
            }
            if (part.EndOfMessage) return buffer.AsMemory(0, size);
        }
    }
}
