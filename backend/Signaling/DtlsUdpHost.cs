using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace ReVerse.Capture.Signaling;

public sealed class DtlsUdpHost : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IPEndPoint bindEndPoint;
    private readonly int maxConnections;
    private readonly int handshakeTimeoutMillis;
    private readonly int queueCapacity;
    private readonly int maxDatagramBytes;
    private readonly TimeSpan shutdownTimeout;
    private readonly Func<byte[], byte[]?> lookupPsk;
    private readonly Action<byte[], DtlsTransport, CancellationToken> connected;
    private readonly Action<string>? stageLog;
    private readonly Action<string, IPEndPoint, ReadOnlyMemory<byte>>? datagramLog;
    private readonly Action<byte[], DtlsTransport, CancellationToken, IPEndPoint>? connectedEndpoint;
    private readonly Action<IPEndPoint, string>? peerEvent;
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<IPEndPoint, Peer> peers = new();
    private Socket? socket;
    private IPEndPoint? localEndPoint;
    private Task completion = Task.CompletedTask;
    private Task cancellationCallbacks = Task.CompletedTask;
    private bool started;
    private bool stopRequested;

    public DtlsUdpHost(
        IPAddress bindAddress,
        int port,
        int maxConnections,
        int handshakeTimeoutSeconds,
        Func<byte[], byte[]?> lookupPsk,
        Action<byte[], DtlsTransport, CancellationToken> connected,
        Action<string>? stageLog = null,
        int queueCapacity = 32,
        int maxDatagramBytes = 2048,
        int shutdownTimeoutSeconds = 5,
        Action<string, IPEndPoint, ReadOnlyMemory<byte>>? datagramLog = null,
        Action<byte[], DtlsTransport, CancellationToken, IPEndPoint>? connectedEndpoint = null,
        Action<IPEndPoint, string>? peerEvent = null)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        ArgumentNullException.ThrowIfNull(lookupPsk);
        ArgumentNullException.ThrowIfNull(connected);
        if (bindAddress.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException("DTLS requires an IPv4 or IPv6 bind address.", nameof(bindAddress));
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (maxConnections is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maxConnections), "DTLS connection limit must be 1–256.");
        if (handshakeTimeoutSeconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeoutSeconds));
        if (queueCapacity is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        if (maxDatagramBytes is < 512 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(maxDatagramBytes));
        if ((long)maxConnections * queueCapacity * maxDatagramBytes > 64 * 1024 * 1024)
            throw new ArgumentException("DTLS total queue budget must not exceed 64 MiB.");
        if (shutdownTimeoutSeconds is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeoutSeconds));

        bindEndPoint = new IPEndPoint(bindAddress, port);
        this.maxConnections = maxConnections;
        handshakeTimeoutMillis = handshakeTimeoutSeconds * 1000;
        this.lookupPsk = lookupPsk;
        this.connected = connected;
        this.stageLog = stageLog;
        this.datagramLog = datagramLog;
        this.connectedEndpoint = connectedEndpoint;
        this.peerEvent = peerEvent;
        this.queueCapacity = queueCapacity;
        this.maxDatagramBytes = maxDatagramBytes;
        shutdownTimeout = TimeSpan.FromSeconds(shutdownTimeoutSeconds);
    }

    public IPEndPoint? LocalEndPoint
    {
        get { lock (gate) return localEndPoint is null ? null : new(localEndPoint.Address, localEndPoint.Port); }
    }

    public bool IsRunning { get { lock (gate) return started && !stopRequested && !completion.IsCompleted; } }
    public int ConnectionCount { get { lock (gate) return peers.Count; } }
    public Task Completion { get { lock (gate) return completion; } }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (started || stopRequested)
                throw new InvalidOperationException("DTLS host cannot be started more than once.");

            var listener = new Socket(bindEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {

                listener.ExclusiveAddressUse = true;
                listener.SendTimeout = 1000;
                listener.ReceiveBufferSize = 256 * 1024;
                if (bindEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                    listener.DualMode = false;
                listener.Bind(bindEndPoint);
                socket = listener;
                localEndPoint = (IPEndPoint)listener.LocalEndPoint!;
                started = true;
                completion = Task.Factory.StartNew(
                    () => Run(listener), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch (SocketException)
            {
                listener.Dispose();
                throw new DtlsHostException("DTLS UDP listener could not bind.");
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RequestStop();
        try
        {
            await Completion.WaitAsync(shutdownTimeout + TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new DtlsHostException("DTLS shutdown deadline exceeded; a callback did not stop.");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Run(Socket listener)
    {
        try
        {
            Log("bound");
            ReceiveLoop(listener);
        }
        catch (ObjectDisposedException) when (IsStopping)
        {

        }
        catch (SocketException) when (IsStopping)
        {

        }
        catch (SocketException)
        {
            throw new DtlsHostException("DTLS UDP listener failed.");
        }
        catch (DtlsHostException)
        {
            throw;
        }
        catch (Exception)
        {

            throw new DtlsHostException("DTLS host failed unexpectedly.");
        }
        finally
        {
            RequestStop();
            DrainWorkers();
        }
    }

    private bool IsStopping { get { lock (gate) return stopRequested; } }

    private void ReceiveLoop(Socket listener)
    {

        var verifier = new DtlsVerifier(new BcTlsCrypto());
        long verifierCreated = Stopwatch.GetTimestamp();
        byte[] buffer = new byte[ushort.MaxValue];
        int cookieReplies = 0;
        int admissions = 0;
        long budgetStarted = Stopwatch.GetTimestamp();
        long lastSweep = budgetStarted;
        while (!IsStopping)
        {

            if (Stopwatch.GetElapsedTime(lastSweep) >= TimeSpan.FromMilliseconds(100))
            {
                SweepPeers();
                lastSweep = Stopwatch.GetTimestamp();
            }
            if (!listener.Poll(100_000, SelectMode.SelectRead))
                continue;

            EndPoint remote = new IPEndPoint(bindEndPoint.AddressFamily == AddressFamily.InterNetwork
                ? IPAddress.Any : IPAddress.IPv6Any, 0);
            int received;
            try
            {
                received = listener.ReceiveFrom(buffer, SocketFlags.None, ref remote);
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.MessageSize
                or SocketError.ConnectionReset or SocketError.ConnectionRefused)
            {

                peerEvent?.Invoke((IPEndPoint)remote, error.SocketErrorCode.ToString());
                continue;
            }
            var endPoint = (IPEndPoint)remote;
            datagramLog?.Invoke("clientToBackend", endPoint, buffer.AsMemory(0, received).ToArray());
            if (received < 13 || received > maxDatagramBytes)
            {
                peerEvent?.Invoke(endPoint, "datagram_size_rejected");
                continue;
            }
            Peer? existing;
            lock (gate) peers.TryGetValue(endPoint, out existing);
            if (existing is not null)
            {

                if (!existing.Transport.TryEnqueue(buffer.AsSpan(0, received)))
                    peerEvent?.Invoke(endPoint, "receive_queue_full_or_closed");
                continue;
            }

            if (ConnectionCount >= maxConnections)
            {
                peerEvent?.Invoke(endPoint, "connection_limit_reached");
                continue;
            }
            if (Stopwatch.GetElapsedTime(budgetStarted) >= TimeSpan.FromSeconds(1))
            {
                budgetStarted = Stopwatch.GetTimestamp();
                cookieReplies = 0;
                admissions = 0;
            }

            if (cookieReplies >= 64 || admissions >= Math.Min(maxConnections, 32))
            {
                peerEvent?.Invoke(endPoint, "admission_rate_limit");
                continue;
            }
            if (Stopwatch.GetElapsedTime(verifierCreated) >= TimeSpan.FromMinutes(1))
            {
                verifier = new DtlsVerifier(new BcTlsCrypto());
                verifierCreated = Stopwatch.GetTimestamp();
            }

            var sender = new CookieSender(listener, endPoint, received, datagramLog);
            DtlsRequest? request = verifier.VerifyRequest(EndpointCookieId(endPoint), buffer, 0, received, sender);
            if (sender.Sent) ++cookieReplies;
            if (request is null)
            {
                peerEvent?.Invoke(endPoint, sender.Sent ? "cookie_challenge_sent" : "handshake_not_admitted");
                continue;
            }

            lock (gate)
            {
                if (stopRequested) break;
                var peer = new Peer(new DtlsDatagramTransport(listener, endPoint, queueCapacity, maxDatagramBytes, datagramLog));
                peers.Add(endPoint, peer);
                peer.Worker = Task.Factory.StartNew(
                    () => Serve(peer, request), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            ++admissions;
        }
    }

    private void Serve(Peer peer, DtlsRequest request)
    {
        using var identity = new DtlsPskIdentityLookup(lookupPsk);
        DtlsTransport? transport = null;
        try
        {
            Log("handshake-started");
            peerEvent?.Invoke(peer.Transport.RemoteEndPoint, "handshake_started");
            transport = new DtlsServerProtocol().Accept(
                new DtlsPskServer(identity, handshakeTimeoutMillis), peer.Transport, request);
            lock (gate)
            {
                if (stopRequested || peer.Transport.IsClosed)
                    return;
                peer.Authenticated = true;
            }
            byte[] authenticatedIdentity = identity.Identity
                ?? throw new DtlsHostException("DTLS handshake completed without a PSK identity.");
            Log("authenticated");
            try
            {
                if (connectedEndpoint is null) connected(authenticatedIdentity, transport, stopping.Token);
                else connectedEndpoint(authenticatedIdentity, transport, stopping.Token, peer.Transport.RemoteEndPoint);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {

            }
            catch (IOException)
            {

                Log("connection-io-closed");
            }
            catch (Exception)
            {
                throw new DtlsHostException("DTLS connected callback failed.");
            }
        }
        catch (IOException) when (identity.CallbackFailure is not null)
        {
            throw identity.CallbackFailure;
        }
        catch (IOException)
        {
            Log(IsStopping ? "connection-stopped" : "handshake-rejected");
            peerEvent?.Invoke(peer.Transport.RemoteEndPoint, IsStopping ? "connection_stopped" : "handshake_rejected");
        }
        finally
        {
            try
            {
                transport?.Close();
            }
            finally
            {
                peer.Transport.Close();
                peerEvent?.Invoke(peer.Transport.RemoteEndPoint, "transport_closed");
            }
        }
    }

    private void SweepPeers()
    {
        KeyValuePair<IPEndPoint, Peer>[] snapshot;
        lock (gate) snapshot = peers.ToArray();
        foreach (var entry in snapshot)
        {
            Peer peer = entry.Value;
            if (peer.Worker.IsCompleted)
            {

                peer.Worker.GetAwaiter().GetResult();
                lock (gate) peers.Remove(entry.Key);
                Log("connection-closed");
            }
            else
            {
                bool timedOut = false;
                lock (gate)
                {
                    if (!peer.Authenticated && !peer.Transport.IsClosed
                        && Stopwatch.GetElapsedTime(peer.Created).TotalMilliseconds >= handshakeTimeoutMillis)
                    {
                        peer.Transport.Close();
                        timedOut = true;
                    }
                }
                if (timedOut)
                {
                    Log("handshake-timeout");
                    peerEvent?.Invoke(entry.Key, "handshake_timeout");
                }
            }
        }
    }

    private void RequestStop()
    {
        lock (gate)
        {
            if (stopRequested) return;
            stopRequested = true;
            localEndPoint = null;
            socket?.Dispose();
            foreach (Peer peer in peers.Values) peer.Transport.Close();

            cancellationCallbacks = stopping.CancelAsync();
        }
    }

    private void DrainWorkers()
    {
        Task[] tasks;
        lock (gate) tasks = peers.Values.Select(peer => peer.Worker).Append(cancellationCallbacks).ToArray();
        try
        {
            Task.WhenAll(tasks).WaitAsync(shutdownTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new DtlsHostException("DTLS shutdown deadline exceeded; a callback did not stop.");
        }
        catch (DtlsHostException)
        {
            throw;
        }
        catch (Exception)
        {

            throw new DtlsHostException("DTLS worker or cancellation callback failed during shutdown.");
        }
        finally
        {
            lock (gate)
            {
                foreach (var entry in peers.Where(entry => entry.Value.Worker.IsCompleted).ToArray())
                    peers.Remove(entry.Key);
            }
        }
    }

    private void Log(string stage)
    {
        try
        {
            stageLog?.Invoke(stage);
        }
        catch (Exception)
        {
            throw new DtlsHostException("DTLS stage log callback failed.");
        }
    }

    private static byte[] EndpointCookieId(IPEndPoint endPoint)
    {
        byte[] address = endPoint.Address.GetAddressBytes();
        byte[] result = new byte[address.Length + 10];
        address.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(address.Length), (ushort)endPoint.Port);
        long scope = endPoint.AddressFamily == AddressFamily.InterNetworkV6 ? endPoint.Address.ScopeId : 0;
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(address.Length + 2), scope);
        return result;
    }

    private sealed class Peer(DtlsDatagramTransport transport)
    {
        internal DtlsDatagramTransport Transport { get; } = transport;
        internal long Created { get; } = Stopwatch.GetTimestamp();
        internal Task Worker { get; set; } = Task.CompletedTask;
        internal bool Authenticated { get; set; }
    }

    private sealed class CookieSender(Socket listener, IPEndPoint remote, int requestBytes,
        Action<string, IPEndPoint, ReadOnlyMemory<byte>>? datagramLog) : DatagramSender
    {
        internal bool Sent { get; private set; }
        public int GetSendLimit() => requestBytes;
        public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));
        public void Send(ReadOnlySpan<byte> buffer)
        {

            if (Sent || buffer.Length > requestBytes) return;
            Sent = true;
            try
            {
                listener.SendTo(buffer, SocketFlags.None, remote);
                datagramLog?.Invoke("backendToClient", remote, buffer.ToArray());
            }
            catch (SocketException)
            {

                throw new IOException("DTLS cookie reply could not be sent.");
            }
            catch (ObjectDisposedException)
            {
                throw new IOException("DTLS UDP socket is closed.");
            }
        }
    }
}

public sealed class DtlsHostException(string message) : Exception(message);
