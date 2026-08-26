using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MiaoNet.Shared;

namespace MiaoNet.ClientShared;

public sealed partial class MiaoServerConnection : IDisposable
{
    private static readonly ArrayPool<byte> pool = ArrayPool<byte>.Shared;

    private readonly Socket socket;
    private readonly SslStream sslStream;

    // TODO we need to stop using this
    private readonly MemoryStream sendMemoryStream;

#if PACKET_TRACING
    private readonly ConcurrentPacketPriorityQueue<DebugQueuedPacket> sendQueue;
    private int debugMaxSendQueueDepth;
    private long debugPacketsSent;
    private long debugBytesSent;
    private long debugTotalQueueWaitTicks;
    private long debugMaxQueueWaitTicks;
    private long debugMaxSerializationTicks;
    private long debugMaxWriteTicks;
    private long debugPlayerFramesSent;
    private long debugPlayerFrameBytesSent;
    private long debugWatchDeltasSent;
    private long debugWatchDeltaBytesSent;
    private readonly object debugSendMetricsLock = new();
#else
    private readonly ConcurrentPacketPriorityQueue<IContextualPacket> sendQueue;
#endif
    private readonly SemaphoreSlim sendSemaphore;

#if PACKET_TRACING
    private readonly record struct DebugQueuedPacket(IContextualPacket Packet, long EnqueuedAt);
#endif

    private MiaoServerConnection(Socket socket, SslStream sslStream)
    {
        this.socket = socket;
        this.sslStream = sslStream;

        sendMemoryStream = new(512);

        sendQueue = new();
        sendSemaphore = new(0);
    }

    public static async Task<MiaoServerConnection> CreateAsync(
        EndPoint endPoint,
        string hostName,
        bool revocationCheck,
        CancellationToken token
    )
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;

        await socket.ConnectAsync(endPoint, token);
        NetworkStream networkStream = new NetworkStream(socket);
        await networkStream.WriteAsync(Connection.HandshakeHead, token);

#if !USE_LOCALHOST_PFX
        var sslStream = new SslStream(networkStream, false, (sender, certificate, chain, errors) =>
        {
            if (errors != SslPolicyErrors.None)
            {
                X509ChainStatusFlags chainStatusFlags = X509ChainStatusFlags.NoError;
                if (chain != null)
                {
                    X509ChainStatus[] chainStatus = chain.ChainStatus;
                    for (int i = 0; i < chainStatus.Length; i++)
                        chainStatusFlags |= (chainStatus[i]).Status;
                }
                throw new MiaoSslException(errors, chainStatusFlags);
            }
            return true;
        });
#else
        var certStream = typeof(MiaoServerConnection).Assembly.GetManifestResourceStream("localhost.pfx")!;
        byte[] certRawData = new byte[certStream.Length];
        certStream.ReadExactly(certRawData, 0, certRawData.Length);
        var cert = new X509Certificate2(certRawData);
        var sslStream = new SslStream(networkStream, false, (sender, certificate, chain, errors) =>
        {
            if (certificate == null) return false;
            var remote = new X509Certificate2(certificate);
            return string.Equals(remote.Thumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase);
        });
#endif
        SslClientAuthenticationOptions options = new()
        {
            TargetHost = hostName,
            EnabledSslProtocols = Connection.AllowedSslProtocols,
            CertificateRevocationCheckMode = revocationCheck
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck
        };

        await sslStream.AuthenticateAsClientAsync(options, token);

        return new(socket, sslStream);
    }

    public async Task<Version?> MakeVersionCheck(Version clientVersion, CancellationToken token)
    {
        ushort major = (ushort)clientVersion.Major;
        ushort minor = (ushort)clientVersion.Minor;
        ushort build = (ushort)clientVersion.Build;

        const int VersionLength = 3 * sizeof(ushort);
        var buffer = pool.Rent(VersionLength);
        try
        {
            var memory = buffer.AsMemory(0, VersionLength);
            var span = memory.Span;
            BinaryPrimitives.WriteUInt16LittleEndian(span[0..2], major);
            BinaryPrimitives.WriteUInt16LittleEndian(span[2..4], minor);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], build);
            await sslStream.WriteAsync(memory, token);
        }
        finally
        {
            pool.Return(buffer);
        }

        buffer = pool.Rent(VersionLength + 1);
        try
        {
            var memory = buffer.AsMemory(0, VersionLength + 1);
            await sslStream.ReadExactlyAsync(memory[0..1], token);
            bool passed = memory.Span[0] != 0;
            if (!passed)
            {
                await sslStream.ReadExactlyAsync(memory[1..(VersionLength + 1)], token);
                var span = memory.Span;
                ushort majorServer = BinaryPrimitives.ReadUInt16LittleEndian(span[1..3]);
                ushort minorServer = BinaryPrimitives.ReadUInt16LittleEndian(span[3..5]);
                ushort buildServer = BinaryPrimitives.ReadUInt16LittleEndian(span[5..7]);
                return new(majorServer, minorServer, buildServer);
            }
            else
            {
                return null;
            }
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    public async Task<HandshakeAckData> MakeHandshakeAsync(HandshakeData handshakeData, CancellationToken token)
    {
        {
            MemoryStream ms = new MemoryStream(64);
            ms.Seek(2, SeekOrigin.Begin);
            RefBinaryWriter writer = new(ms);
            writer.Write(handshakeData);
            ushort size = (ushort)(ms.Position - sizeof(ushort));
            ms.Seek(0, SeekOrigin.Begin);
            writer.Write(size);
            var memory = ms.GetBuffer().AsMemory(0, size + sizeof(ushort));
            await sslStream.WriteAsync(memory, token);
        }

        {
            ushort size;
            var buffer = pool.Rent(sizeof(ushort));
            try
            {
                var memory = buffer.AsMemory(0, sizeof(ushort));
                await sslStream.ReadExactlyAsync(memory, token);
                size = BinaryPrimitives.ReadUInt16LittleEndian(memory.Span);
            }
            finally
            {
                pool.Return(buffer);
            }

            buffer = pool.Rent(size);
            try
            {
                var memory = buffer.AsMemory(0, size);
                await sslStream.ReadExactlyAsync(memory, token);

                RefBinaryReader reader = new(memory.Span);
                var ack = reader.Read<HandshakeAckData>();
                return ack;
            }
            finally
            {
                pool.Return(buffer);
            }
        }
    }

    // TODO this method can interrupt send/receive Task and cause exceptions
    // but we have to make it async if we're going to wait for it
    // at least the usage in MiaoNet.Client ensure that token passed to send/receive Task
    // is cancelled before this call
    public void Close(bool shutdown)
    {
        sendSemaphore.Dispose();
        sslStream.Dispose();
        if (shutdown)
            socket.Shutdown(SocketShutdown.Both);
        socket.Dispose();
    }

    public void Dispose()
    {
        Close(false);
    }

    public async Task SendPacketsLoopAsync(IPacketSerializationContext context, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            while (TryDequeuePacket(out IContextualPacket packet, out long enqueuedAt))
                await SendPacketAsync(packet, enqueuedAt, context, token);
            if (token.IsCancellationRequested)
                return;
            await sendSemaphore.WaitAsync(token);
        }
    }

    // hmmm I think using async enumerable is somehow not the best way
    // but I can't think up any better
    public async IAsyncEnumerable<IContextualPacket> ReceivePacketsLoopAsync(
        IPacketSerializationContext context,
        [EnumeratorCancellation] CancellationToken token
    )
    {
        byte[] headerBuffer = new byte[Connection.PacketHeaderSize];
        while (!token.IsCancellationRequested)
        {
            IContextualPacket? packet = await PacketFraming.ReadPacketAsync(
                sslStream,
                headerBuffer,
                context,
                token
            );

            if (packet is null)
                yield break;
            else
                yield return packet;
        }
    }

    public int QueuePacket(IContextualPacket packet)
    {
        PacketPriority priority = PacketPriorityClassifier.Classify(packet);
#if PACKET_TRACING
        sendQueue.Enqueue(priority, new(packet, Stopwatch.GetTimestamp()));
#else
        sendQueue.Enqueue(priority, packet);
#endif
        int count = sendQueue.Count;
#if PACKET_TRACING
        UpdateMaximum(ref debugMaxSendQueueDepth, count);
#endif
        sendSemaphore.Release();
        return count;
    }

    private bool TryDequeuePacket(
        out IContextualPacket packet,
        out long enqueuedAt
    )
    {
#if PACKET_TRACING
        if (sendQueue.TryDequeue(out DebugQueuedPacket queuedPacket))
        {
            packet = queuedPacket.Packet;
            enqueuedAt = queuedPacket.EnqueuedAt;
            return true;
        }
#else
        if (sendQueue.TryDequeue(out IContextualPacket? dequeuedPacket))
        {
            packet = dequeuedPacket!;
            enqueuedAt = 0;
            return true;
        }
#endif
        packet = null!;
        enqueuedAt = 0;
        return false;
    }

    private async Task SendPacketAsync(
        IContextualPacket packet,
        long enqueuedAt,
        IPacketSerializationContext context,
        CancellationToken token
    )
    {
#if PACKET_TRACING
        long sendStartedAt = Stopwatch.GetTimestamp();
#endif
        sendMemoryStream.Position = 0;
        PacketFraming.WritePacket(sendMemoryStream, packet, context);
        int frameSize = checked((int)sendMemoryStream.Position);
#if PACKET_TRACING
        long serializationCompletedAt = Stopwatch.GetTimestamp();
#endif
        await sslStream.WriteAsync(sendMemoryStream.GetBuffer().AsMemory(0, frameSize), token);
#if PACKET_TRACING
        long writeCompletedAt = Stopwatch.GetTimestamp();
        RecordDebugSend(
            packet,
            frameSize,
            sendStartedAt - enqueuedAt,
            serializationCompletedAt - sendStartedAt,
            writeCompletedAt - serializationCompletedAt
        );
#endif
    }

#if PACKET_TRACING
    public MiaoTransportDebugSnapshot ConsumeDebugSnapshot()
    {
        MiaoQueueDebugState queue = GetDebugQueueState();
        int maxQueueDepth = Interlocked.Exchange(ref debugMaxSendQueueDepth, queue.Depth);
        lock (debugSendMetricsLock)
        {
            MiaoTransportDebugSnapshot snapshot = new(
                queue.Depth,
                maxQueueDepth,
                queue.OldestPacketAgeMilliseconds,
                debugPacketsSent,
                debugBytesSent,
                GetAverageMilliseconds(debugTotalQueueWaitTicks, debugPacketsSent),
                ToMilliseconds(debugMaxQueueWaitTicks),
                ToMilliseconds(debugMaxSerializationTicks),
                ToMilliseconds(debugMaxWriteTicks),
                debugPlayerFramesSent,
                debugPlayerFrameBytesSent,
                debugWatchDeltasSent,
                debugWatchDeltaBytesSent
            );
            debugPacketsSent = 0;
            debugBytesSent = 0;
            debugTotalQueueWaitTicks = 0;
            debugMaxQueueWaitTicks = 0;
            debugMaxSerializationTicks = 0;
            debugMaxWriteTicks = 0;
            debugPlayerFramesSent = 0;
            debugPlayerFrameBytesSent = 0;
            debugWatchDeltasSent = 0;
            debugWatchDeltaBytesSent = 0;
            return snapshot;
        }
    }

    public MiaoQueueDebugState GetDebugQueueState()
    {
        long oldestEnqueuedAt = long.MaxValue;
        foreach (PacketPriority priority in Enum.GetValues<PacketPriority>())
        {
            if (sendQueue.TryPeek(priority, out DebugQueuedPacket packet))
                oldestEnqueuedAt = Math.Min(oldestEnqueuedAt, packet.EnqueuedAt);
        }
        double oldestPacketAgeMilliseconds = oldestEnqueuedAt == long.MaxValue
            ? 0d
            : ToMilliseconds(Stopwatch.GetTimestamp() - oldestEnqueuedAt);
        return new(sendQueue.Count, oldestPacketAgeMilliseconds);
    }

    private void RecordDebugSend(
        IContextualPacket packet,
        int frameSize,
        long queueWaitTicks,
        long serializationTicks,
        long writeTicks
    )
    {
        lock (debugSendMetricsLock)
        {
            debugPacketsSent++;
            debugBytesSent += frameSize;
            debugTotalQueueWaitTicks += queueWaitTicks;
            debugMaxQueueWaitTicks = Math.Max(debugMaxQueueWaitTicks, queueWaitTicks);
            debugMaxSerializationTicks = Math.Max(
                debugMaxSerializationTicks,
                serializationTicks
            );
            debugMaxWriteTicks = Math.Max(debugMaxWriteTicks, writeTicks);

            if (packet is PacketPlayerFrame)
            {
                debugPlayerFramesSent++;
                debugPlayerFrameBytesSent += frameSize;
            }
            else if (packet is PacketWatchSceneDelta)
            {
                debugWatchDeltasSent++;
                debugWatchDeltaBytesSent += frameSize;
            }
        }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target))
            && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static double GetAverageMilliseconds(long totalTicks, long sampleCount)
        => sampleCount == 0 ? 0d : ToMilliseconds(totalTicks) / sampleCount;

    private static double ToMilliseconds(long ticks)
        => ticks * 1000d / Stopwatch.Frequency;
#endif
}

#if PACKET_TRACING
public readonly record struct MiaoQueueDebugState(
    int Depth,
    double OldestPacketAgeMilliseconds
);

public readonly record struct MiaoTransportDebugSnapshot(
    int CurrentQueueDepth,
    int MaxQueueDepth,
    double OldestPacketAgeMilliseconds,
    long PacketsSent,
    long BytesSent,
    double AverageQueueWaitMilliseconds,
    double MaxQueueWaitMilliseconds,
    double MaxSerializationMilliseconds,
    double MaxWriteMilliseconds,
    long PlayerFramesSent,
    long PlayerFrameBytesSent,
    long WatchDeltasSent,
    long WatchDeltaBytesSent
);
#endif
