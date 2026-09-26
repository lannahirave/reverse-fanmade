using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Org.BouncyCastle.Tls;

namespace ReVerse.Capture.Signaling;


internal sealed class DtlsDatagramTransport(
    Socket socket, IPEndPoint remoteEndPoint, int queueCapacity, int maxDatagramBytes,
    Action<string, IPEndPoint, ReadOnlyMemory<byte>>? datagramLog = null) : DatagramTransport
{
    internal IPEndPoint RemoteEndPoint => remoteEndPoint;
    private readonly object gate = new();
    private readonly Queue<byte[]> incoming = new();
    private bool closed;

    internal bool IsClosed { get { lock (gate) return closed; } }

    internal bool TryEnqueue(ReadOnlySpan<byte> datagram)
    {
        lock (gate)
        {
            if (closed || incoming.Count >= queueCapacity || datagram.Length > maxDatagramBytes)
                return false;
            incoming.Enqueue(datagram.ToArray());
            Monitor.Pulse(gate);
            return true;
        }
    }

    public int GetReceiveLimit() => maxDatagramBytes;
    public int GetSendLimit() => maxDatagramBytes;

    public int Receive(byte[] buf, int off, int len, int waitMillis) =>
        Receive(buf.AsSpan(off, len), waitMillis);

    public int Receive(Span<byte> buffer, int waitMillis)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(waitMillis);
        long start = Stopwatch.GetTimestamp();
        lock (gate)
        {
            while (true)
            {
                if (closed)
                    throw new IOException("DTLS datagram transport is closed.");

                if (incoming.TryDequeue(out byte[]? datagram))
                {

                    int length = Math.Min(buffer.Length, datagram.Length);
                    datagram.AsSpan(0, length).CopyTo(buffer);
                    return length;
                }


                if (waitMillis == 0)
                {
                    Monitor.Wait(gate);
                    continue;
                }

                int remaining = waitMillis - (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (remaining <= 0)
                    return -1;
                Monitor.Wait(gate, remaining);
            }
        }
    }

    public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));

    public void Send(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > maxDatagramBytes)
            throw new IOException("DTLS datagram exceeds the configured limit.");
        lock (gate)
        {
            if (closed)
                throw new IOException("DTLS datagram transport is closed.");
        }

        try
        {
            int sent = socket.SendTo(buffer, SocketFlags.None, remoteEndPoint);
            if (sent != buffer.Length)
                throw new IOException("DTLS datagram could not be sent completely.");
            datagramLog?.Invoke("backendToClient", remoteEndPoint, buffer.ToArray());
        }
        catch (SocketException)
        {
            throw new IOException("DTLS UDP send failed.");
        }
        catch (ObjectDisposedException)
        {
            throw new IOException("DTLS UDP socket is closed.");
        }
    }

    public void Close()
    {
        lock (gate)
        {
            closed = true;
            incoming.Clear();
            Monitor.PulseAll(gate);
        }
    }
}
