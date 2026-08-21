using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MiaoNet.ClientShared;
using MiaoNet.Shared;

namespace Celeste.Mod.MiaoNet;

// TODO this is ugly, we need a refactor on this
partial class MiaoNetContext
{
#if PACKET_TRACING
    private const int MaxPacketTraceLength = 4096;

    private static void TracePacketSafely(
        IContextualPacket packet,
        System.Text.Json.JsonSerializerOptions options
    )
    {
        string typeName = packet.GetType().ToString();
        if (typeName.Contains("Frame", StringComparison.Ordinal)
            || typeName.Contains("PingData", StringComparison.Ordinal)
            || typeName.Contains("UpdateOnlineStatus", StringComparison.Ordinal)
            || typeName.Contains("PlayedAudio", StringComparison.Ordinal)
            || typeName.Contains("PacketPing", StringComparison.Ordinal))
            return;

        try
        {
            string json = SerializePacketTrace(packet, options);
            if (json.Length > MaxPacketTraceLength)
                json = string.Concat(json.AsSpan(0, MaxPacketTraceLength), "... [truncated]");
            Console.WriteLine($"== Type: {packet.GetType()} ==");
            Console.WriteLine(json);
        }
        catch
        {
            // Packet tracing is diagnostic only and must never terminate the receive loop.
        }
    }

    private static string SerializePacketTrace(
        IContextualPacket packet,
        System.Text.Json.JsonSerializerOptions options
    )
        => packet switch
        {
            PacketWatchStartResponse response => System.Text.Json.JsonSerializer.Serialize(new
            {
                response.RequestID,
                response.Result,
                response.SessionID,
                Snapshot = response.Snapshot is null ? null : SummarizeSnapshot(response.Snapshot),
            }, options),
            PacketWatchSnapshotResponse response => System.Text.Json.JsonSerializer.Serialize(new
            {
                response.RequestID,
                response.Result,
                Snapshot = response.Snapshot is null ? null : SummarizeSnapshot(response.Snapshot),
            }, options),
            PacketWatchSceneDelta packetDelta => System.Text.Json.JsonSerializer.Serialize(new
            {
                Delta = SummarizeDelta(packetDelta.Delta),
            }, options),
            PacketWatchSceneDeltaNotification notification => System.Text.Json.JsonSerializer.Serialize(new
            {
                notification.SessionID,
                notification.TargetPlayerID,
                Delta = SummarizeDelta(notification.Delta),
            }, options),
            _ => System.Text.Json.JsonSerializer.Serialize((object)packet, options),
        };

    private static object SummarizeSnapshot(WatchSceneSnapshot snapshot)
        => new
        {
            snapshot.Sequence,
            snapshot.Location,
            FlagCount = snapshot.Flags.Count,
            TouchSwitchCount = snapshot.ActiveTouchSwitchIDs.Count,
            EntityStateCount = snapshot.EntityStates.Count,
            EntityKinds = CountEntityKinds(snapshot.EntityStates.Select(state => state.Key.Kind)),
        };

    private static object SummarizeDelta(WatchSceneDelta delta)
        => new
        {
            delta.Sequence,
            delta.Location,
            AddedFlagCount = delta.AddedFlags.Count,
            RemovedFlagCount = delta.RemovedFlags.Count,
            delta.RequiresRoomReload,
            delta.HasTouchSwitchState,
            TouchSwitchCount = delta.ActiveTouchSwitchIDs.Count,
            delta.EntityStateMode,
            EntityStateCount = delta.EntityStates.Count,
            EntityEventCount = delta.EntityEvents.Count,
            EntityKinds = CountEntityKinds(delta.EntityStates.Select(state => state.Key.Kind)),
            EventKinds = CountEntityKinds(delta.EntityEvents.Select(entityEvent => entityEvent.Key.Kind)),
        };

    private static IReadOnlyDictionary<WatchEntityKind, int> CountEntityKinds(
        IEnumerable<WatchEntityKind> kinds
    )
        => kinds.GroupBy(kind => kind).ToDictionary(group => group.Key, group => group.Count());
#endif

    private void ConnectionThread(object? param)
    {
        var connectionToken = (CancellationToken)param!;

        if (connectionToken.IsCancellationRequested)
            return;

        SingleThreadedSynchronizationContext syncCtx = new();
        SingleThreadedTaskScheduler taskScheduler = new(syncCtx);
        SynchronizationContext.SetSynchronizationContext(syncCtx);

        CancellationTokenSource threadCts = new();
        _ = StartConnectionAsync(this, connectionToken).ContinueWith(t =>
        {
            threadCts.Cancel();
            if (t.IsFaulted)
            {
                Logger.Error(LT.MiaoNetConnection, "Unhandled exception in connection thread!");
                // throw to main thread
                mainThreadQueue.Enqueue(() => throw t.Exception);
            }
        }, taskScheduler);

        try
        {
            syncCtx.ProcessLoop(threadCts.Token);
        }
        catch (OperationCanceledException e)
        when (e.CancellationToken == threadCts.Token)
        {
            Logger.Info(LT.MiaoNetConnection, "Connection thread cancelled.");
            return;
        }
        finally
        {
            threadCts.Dispose();
        }

        Logger.Info(LT.MiaoNetConnection, "Connection thread exited.");
        return;

        async Task StartConnectionAsync(IPacketSerializationContext context, CancellationToken token)
        {
#if USE_CELEMIAO_AUTH
            if (MiaoNetModule.Settings.TokenData is null or { Length: 0 } && ClientRC.AuthenticationCode is null)
            {
                QueueDisconnectStatus(Dialog.Get("miaonet_connection_status_no_token"));
                return;
            }
#else
            if (string.IsNullOrEmpty(MiaoNetModule.Settings.Name))
            {
                QueueDisconnectStatus(Dialog.Get("miaonet_connection_status_no_name"));
                return;
            }
#endif

            string host = TargetServer;
            int Port = TargetPort;

            EndPoint ep = IPAddress.TryParse(host, out var ipa)
                ? new IPEndPoint(ipa, Port)
                : new DnsEndPoint(host, Port);

            LanguageCode langCode = GameLanguage.GetLanguageCode(Dialog.Language.Id);
            HandshakeData.NetMod[] netMods = [];

            HandshakeData handshakeData;

#if USE_CELEMIAO_AUTH
            if (ClientRC.AuthenticationCode is null)
            {
                handshakeData = new HandshakeData(langCode, false, MiaoNetModule.Settings.TokenData!, netMods);
            }
            else
            {
                Logger.Info(LT.MiaoNetConnection, "Auth code is not null, set isAuthorize to true to log in.");
                handshakeData = new HandshakeData(langCode, true, Encoding.UTF8.GetBytes(ClientRC.AuthenticationCode), netMods);
            }

            ClientRC.AuthenticationCode = null;
#else
            var settings = MiaoNetModule.Settings;
            string name = settings.Name;
            string? prefix = settings.Prefix;
            Color color = settings.Color is null ? Color.White : Calc.HexToColor(settings.Color);
            PlayerInfo playerInfo = new(-1, name, prefix ?? string.Empty, settings.AvatarUrl ?? string.Empty, color);
            MemoryStream ms = new(32);
            RefBinaryWriter writer = new(ms);
            writer.Write(playerInfo);
            byte[] authData = ms.GetBuffer().AsSpan(0, checked((int)ms.Position)).ToArray();
            handshakeData = new(langCode, false, authData, netMods);
#endif

            Logger.Info(LT.MiaoNetConnection, $"Trying connecting to {ep}...");
            MiaoServerConnection? connection = null;

            IAsyncEnumerator<IContextualPacket> packetsAsyncEnumerator;
            try
            {
                bool revocationCheck = !MiaoNetModule.Settings.IgnoreCertRevocationStatus;
                connection = await MiaoServerConnection.CreateAsync(ep, TargetServer, revocationCheck, token);

                Version localVersion = Connection.Version;
                Version? version = await connection.MakeVersionCheck(localVersion, token);
                if (version is not null)
                {
                    connection.Close(true);
                    QueueDisconnectStatus(ConnectionStatus.VersionNotMatch(localVersion, version));
                    return;
                }
                else
                {
                    QueueStatus(ConnectionStatus.Authenticating);
                }

                HandshakeAckData handshakeAck = await connection.MakeHandshakeAsync(handshakeData, token);
                var r = handshakeAck.AuthenticationResultType;
                if (r != AuthenticationResultType.Success)
                {
                    connection.Close(true);
                    string? reason = handshakeAck.DeniedReason;
                    if (reason is not null)
                    {
                        QueueDisconnectStatus(reason);
                    }
                    if (r == AuthenticationResultType.InvalidTokenData)
                    {
                        QueueDisconnectStatus(ConnectionStatus.InvalidTokenData);
                    }
                    else if (r == AuthenticationResultType.InternalServerError)
                    {
                        QueueDisconnectStatus(ConnectionStatus.InternalServerError);
                    }
                    return;
                }

#if USE_CELEMIAO_AUTH
                if (handshakeAck.AuthenticationData is not null)
                {
                    MiaoNetModule.Settings.TokenData = handshakeAck.AuthenticationData;
                    Logger.Info(LT.MiaoNetConnection, "Server sent new auth data, accepted.");
                }
#endif

                packetsAsyncEnumerator = connection.ReceivePacketsLoopAsync(context, token).GetAsyncEnumerator(token);

                await packetsAsyncEnumerator.MoveNextAsync();
                IContextualPacket? packetInitial = packetsAsyncEnumerator.Current;
                if (packetInitial is not PacketClientInitial clientInitial)
                {
                    if (packetInitial is null)
                        Logger.Warn(LT.MiaoNetConnection, $"Remote sent empty or invalid initial reply.");
                    else
                        Logger.Warn(LT.MiaoNetConnection, $"Remote sent a weird initial packet {packetInitial.GetType()}.");
                    connection.Close(false);
                    QueueDisconnectStatus(ConnectionStatus.DisconnectedExceptionally);
                    return;
                }
                else
                {
                    Logger.Info(LT.MiaoNetConnection, $"Connected to {ep}.");
                    TaskCompletionSource ackTaskSource = new();
                    mainThreadQueue.Enqueue(() =>
                    {
                        OnInitialized(connection, clientInitial);
                        ackTaskSource.SetResult();
                    });
                    // wait until the main thread ack we've finished connecting
                    await ackTaskSource.Task;
                    if (ShowAvatar)
                    {
                        foreach (var p in clientInitial.Players)
                            _ = SafePrepareAvatarAsync(p.PlayerID, p.PlayerInfo);
                        _ = SafePrepareAvatarAsync(clientInitial.PlayerID, clientInitial.SelfPlayerInfo);
                    }
                }

            }
            catch (OperationCanceledException e)
            when (e.CancellationToken == token)
            {
                connection?.Close(false);
                Logger.Info(LT.MiaoNetConnection, "Connection cancelled");
                QueueDisconnectStatus(ConnectionStatus.Cancelled);
                return;
            }
            catch (MiaoSslException e)
            {
                connection?.Close(false);
                Logger.Error(LT.MiaoNetConnection, $"Ssl error: {e.SslPolicyErrors}. {e.X509ChainStatusFlags}");
                Logger.LogDetailed(e, LT.MiaoNetConnection);
                if (e.X509ChainStatusFlags.HasFlag(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation))
                    QueueDisconnectStatus(ConnectionStatus.ConnectionSslRevocationCheckFailed);
                else
                    QueueDisconnectStatus(ConnectionStatus.ConnectionSslError(e.SslPolicyErrors, e.X509ChainStatusFlags));
                return;
            }
            catch (Exception e)
            {
                connection?.Close(false);
                SocketException? se = (e as IOException)?.InnerException as SocketException;
                Logger.Error(LT.MiaoNetConnection, $"Error when connecting: {e}");
                QueueDisconnectStatus(ConnectionStatus.ConnectFailedWithReason((se ?? e).Message));
                return;
            }

            try
            {
                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    Task receiveTask = DoReceivingAndProcessingAsync(packetsAsyncEnumerator, context, cts.Token);
                    Task sendTask = connection.SendPacketsLoopAsync(context, cts.Token);

                    Task task = await Task.WhenAny(receiveTask, sendTask);
                    if (task.IsFaulted)
                        await task;
                    cts.Cancel();

                    async Task DoReceivingAndProcessingAsync(
                        IAsyncEnumerator<IContextualPacket> packets,
                        IPacketSerializationContext context,
                        CancellationToken token
                    )
                    {
#if PACKET_TRACING
                        System.Text.Json.JsonSerializerOptions options = new()
                        {
                            IncludeFields = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
                            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                        };
#endif
                        while (await packets.MoveNextAsync())
                        {
                            var packet = packets.Current;

                            if (!HandleDirectPacket(packet))
                                receiveQueue.Enqueue(packet);
#if PACKET_TRACING
                            TracePacketSafely(packet, options);
#endif
                        }
                    }
                }
            }
            catch (OperationCanceledException e)
            when (e.CancellationToken == token)
            {
                Logger.Info(LT.MiaoNetConnection, "Connection cancelled.");
                QueueDisconnectStatus(ConnectionStatus.Cancelled);
                return;
            }
            catch (Exception e)
            {
                Logger.Error(LT.MiaoNetConnection, $"Error during connection: {e}");
                if (e is IOException && e.InnerException is SocketException se)
                    e = se;
                QueueDisconnectStatus(ConnectionStatus.DisconnectedWithReason(e.Message));
                return;
            }
        }

        void QueueDisconnectStatus(string statusMessage)
        {
            mainThreadQueue.Enqueue(() =>
            {
                StatusComponent.ShowStatusMessage(statusMessage);
                OnDisconnected();
            });
        }

        void QueueStatus(string statusMessage)
            => mainThreadQueue.Enqueue(() => StatusComponent.ShowStatusMessage(statusMessage, true));
    }
}
