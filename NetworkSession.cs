using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Godot;

public partial class NetworkSession : Node
{
    public enum SessionMode { OfflineAi, Host, Client, DedicatedServer, DedicatedClient }
    private enum LobbyPhase { Lobby, InGame, Rematch }

    public const int DefaultPort = 24872;
    private const int P2pMaxClients = 1;
    private const int DedicatedMaxClients = 2;

    public static NetworkSession Instance { get; private set; } = null!;

    public SessionMode Mode { get; private set; } = SessionMode.OfflineAi;
    public bool IsNetworkGame => Mode != SessionMode.OfflineAi;
    public bool IsHost => Mode == SessionMode.Host;
    public bool IsDedicatedServer => Mode == SessionMode.DedicatedServer;
    public bool IsAuthority => IsHost || IsDedicatedServer;
    public bool IsClient => Mode is SessionMode.Client or SessionMode.DedicatedClient;
    public bool IsDedicatedClient => Mode == SessionMode.DedicatedClient;
    public bool IsAwaitingRematch => _phase == LobbyPhase.Rematch;
    public bool ConnectionActive { get; private set; }
    public bool LocalReady { get; private set; }
    public bool RemoteReady { get; private set; }
    public long RemotePeerId { get; private set; }
    public int LocalPlayerSlot { get; private set; }
    public int ConnectedPlayers { get; private set; }
    public string Status { get; private set; } = "AI 대전을 선택하거나 방을 생성하세요.";
    public string LocalLanAddress { get; private set; } = "127.0.0.1";
    public string HostRoomCode { get; private set; } = string.Empty;

    public event Action? LobbyChanged;
    public event Action<int, PlayerCommand>? RemoteInputReceived;
    public event Action<byte[]>? SnapshotReceived;
    public event Action? RematchStarted;
    public event Action<string>? ConnectionClosed;

    private readonly Dictionary<long, int> _peerSlots = new();
    private readonly Dictionary<long, bool> _peerReady = new();
    private readonly Dictionary<long, uint> _lastInputSequence = new();
    private readonly Dictionary<long, uint> _lastClientTick = new();
    private readonly Dictionary<long, CharacterLook> _peerLooks = new();
    private readonly List<PlayerCommand> _pendingInputs = new();
    private LobbyPhase _phase;
    private bool _autoReady;
    private bool _sceneTransitionQueued;
    private uint _nextInputSequence;
    private uint _clientTick;

    public override void _Ready()
    {
        Instance = this;
        LocalLanAddress = FindLanIpv4Address();
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;

        var dedicated = false;
        var dedicatedPort = DefaultPort;
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument == "--paakuri-auto-ready")
                _autoReady = true;
            else if (argument == "--paakuri-dedicated")
                dedicated = true;
            else if (argument.StartsWith("--paakuri-dedicated=") && int.TryParse(argument["--paakuri-dedicated=".Length..], out var parsedDedicatedPort))
            {
                dedicated = true;
                dedicatedPort = Mathf.Clamp(parsedDedicatedPort, 1024, 65535);
            }
            else if (argument == "--paakuri-host")
                Host();
            else if (argument.StartsWith("--paakuri-host=") && int.TryParse(argument["--paakuri-host=".Length..], out var hostPort))
                Host(Mathf.Clamp(hostPort, 1024, 65535));
            else if (argument.StartsWith("--paakuri-join="))
                Join(argument["--paakuri-join=".Length..]);
            else if (argument == "--paakuri-ai")
                CallDeferred(MethodName.StartOfflineAi);
            else if (argument == "--paakuri-protocol-self-check")
                Callable.From(RunProtocolSelfCheck).CallDeferred();
        }
        if (dedicated)
        {
            var port = dedicatedPort;
            Callable.From(() => StartDedicatedServer(port)).CallDeferred();
        }
    }

    public void StartOfflineAi()
    {
        Disconnect();
        Mode = SessionMode.OfflineAi;
        GetTree().ChangeSceneToFile("res://Scene/main.tscn");
    }

    public Error Host(int port = DefaultPort)
    {
        Disconnect();
        ENetMultiplayerPeer? peer = null;
        var error = Error.CantCreate;
        var selectedPort = port;
        for (var offset = 0; offset < 10; offset++)
        {
            selectedPort = port + offset;
            if (!CanBindUdpPort(selectedPort))
                continue;
            peer = new ENetMultiplayerPeer();
            error = peer.CreateServer(selectedPort, P2pMaxClients, 3);
            if (error == Error.Ok)
                break;
            peer.Close();
            peer = null;
        }
        if (error != Error.Ok || peer is null)
        {
            SetStatus($"UDP {port}~{port + 9} 방 생성 실패: {error}");
            return error;
        }

        Multiplayer.MultiplayerPeer = peer;
        Mode = SessionMode.Host;
        LocalPlayerSlot = 1;
        ConnectedPlayers = 1;
        ConnectionActive = true;
        HostRoomCode = $"{LocalLanAddress}:{selectedPort}";
        SetStatus($"LAN 방 코드: {HostRoomCode} — 참가자 대기 중");
        GD.Print($"PAAKURI host listening on UDP {selectedPort}; LAN room code {HostRoomCode}");
        return Error.Ok;
    }

    public Error StartDedicatedServer(int port = DefaultPort)
    {
        Disconnect();
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateServer(port, DedicatedMaxClients, 3);
        if (error != Error.Ok)
        {
            SetStatus($"Dedicated server UDP {port} 생성 실패: {error}");
            return error;
        }

        Multiplayer.MultiplayerPeer = peer;
        Mode = SessionMode.DedicatedServer;
        ConnectionActive = true;
        HostRoomCode = $"{LocalLanAddress}:{port}";
        SetStatus($"Dedicated server {HostRoomCode} — 0/2 players");
        GD.Print($"PAAKURI dedicated server listening on UDP {port}");
        GetTree().ChangeSceneToFile("res://Scene/dedicated_server.tscn");
        return Error.Ok;
    }

    public Error Join(string roomCode)
    {
        Disconnect();
        ParseRoomCode(roomCode, out var address, out var port);
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(address, port, 3);
        if (error != Error.Ok)
        {
            SetStatus($"참가 실패: {error}");
            return error;
        }

        Multiplayer.MultiplayerPeer = peer;
        Mode = SessionMode.Client;
        LocalPlayerSlot = 2;
        RemotePeerId = 1;
        SetStatus($"{address}:{port} 연결 중...");
        GD.Print($"PAAKURI client connecting to {address}:{port}");
        return Error.Ok;
    }

    public void ToggleReady()
    {
        if (!IsNetworkGame || !ConnectionActive || IsDedicatedServer || (IsClient && RemotePeerId == 0))
            return;
        ShareLocalCustomization();
        LocalReady = !LocalReady;
        if (IsHost)
        {
            BroadcastLobbyState();
            TryStartOrRematch();
        }
        else
        {
            RpcId(1, MethodName.SubmitReady, LocalReady, _phase == LobbyPhase.Rematch);
        }
        LobbyChanged?.Invoke();
    }

    public PlayerCommand SendInput(PlayerCommand command)
    {
        if (!IsClient || !ConnectionActive || _phase != LobbyPhase.InGame)
            return command.Sanitized();
        var stamped = command.Sanitized() with
        {
            Sequence = ++_nextInputSequence,
            ClientTick = ++_clientTick
        };
        _pendingInputs.Add(stamped);
        if (_pendingInputs.Count > 256)
            _pendingInputs.RemoveAt(0);
        RpcId(1, MethodName.SubmitInput, NetworkProtocol.EncodeInputPacket(_pendingInputs));
        return stamped;
    }

    public void AcknowledgeInputs(uint acknowledgedSequence)
    {
        _pendingInputs.RemoveAll(command => command.Sequence == acknowledgedSequence || NetworkProtocol.IsNewer(acknowledgedSequence, command.Sequence));
    }

    public void BroadcastSnapshot(byte[] payload)
    {
        if (!IsAuthority || _phase == LobbyPhase.Lobby)
            return;
        foreach (var peerId in _peerSlots.Keys)
            RpcId(peerId, MethodName.ReceiveSnapshot, payload);
    }

    public void ShareLocalCustomization()
    {
        if (!IsNetworkGame || !ConnectionActive || IsDedicatedServer ||
            !GodotObject.IsInstanceValid(CharacterCustomization.Instance))
            return;

        var look = CharacterCustomization.Instance.LocalLook.Sanitized();
        if (IsHost)
        {
            foreach (var peerId in _peerSlots.Keys)
                SendCustomization(peerId, 1, look);
            return;
        }

        if (IsClient)
        {
            RpcId(1, MethodName.SubmitCustomization,
                look.BodyColor.R, look.BodyColor.G, look.BodyColor.B,
                look.EyeColor.R, look.EyeColor.G, look.EyeColor.B,
                (int)look.EyeShape, look.EyeFollowLevel);
        }
    }

    public void BeginRematchLobby()
    {
        if (!IsDedicatedServer || _phase == LobbyPhase.Rematch)
            return;
        _phase = LobbyPhase.Rematch;
        LocalReady = false;
        foreach (var peerId in _peerSlots.Keys)
            _peerReady[peerId] = false;
        BroadcastLobbyState();
    }

    public void ReturnToLobby(string reason)
    {
        Disconnect();
        Status = reason;
        ConnectionClosed?.Invoke(reason);
        GetTree().ChangeSceneToFile("res://Scene/start.tscn");
    }

    public void Disconnect()
    {
        if (Multiplayer.MultiplayerPeer is { } peer)
            peer.Close();
        Multiplayer.MultiplayerPeer = null;
        Mode = SessionMode.OfflineAi;
        ConnectionActive = false;
        LocalReady = false;
        RemoteReady = false;
        RemotePeerId = 0;
        LocalPlayerSlot = 0;
        ConnectedPlayers = 0;
        HostRoomCode = string.Empty;
        _phase = LobbyPhase.Lobby;
        _sceneTransitionQueued = false;
        _peerSlots.Clear();
        _peerReady.Clear();
        _lastInputSequence.Clear();
        _lastClientTick.Clear();
        _peerLooks.Clear();
        _pendingInputs.Clear();
        _nextInputSequence = 0;
        _clientTick = 0;
        if (GodotObject.IsInstanceValid(CharacterCustomization.Instance))
            CharacterCustomization.Instance.ResetRemote();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 2)]
    private void SubmitReady(bool ready, bool rematch)
    {
        if (!IsAuthority)
            return;
        var peerId = Multiplayer.GetRemoteSenderId();
        if (!_peerSlots.ContainsKey(peerId) || rematch != (_phase == LobbyPhase.Rematch) || _phase == LobbyPhase.InGame)
            return;
        _peerReady[peerId] = ready;
        RemoteReady = ready;
        BroadcastLobbyState();
        TryStartOrRematch();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 2)]
    private void SubmitCustomization(
        float bodyR,
        float bodyG,
        float bodyB,
        float eyeR,
        float eyeG,
        float eyeB,
        int eyeShape,
        int eyeFollowLevel)
    {
        if (!IsAuthority)
            return;

        var peerId = Multiplayer.GetRemoteSenderId();
        if (!_peerSlots.TryGetValue(peerId, out var playerSlot))
            return;

        var look = CharacterCustomization.FromNetwork(
            bodyR, bodyG, bodyB,
            eyeR, eyeG, eyeB,
            eyeShape, eyeFollowLevel);
        _peerLooks[peerId] = look;

        if (IsHost && GodotObject.IsInstanceValid(CharacterCustomization.Instance))
            CharacterCustomization.Instance.SetRemote(look);

        foreach (var targetPeerId in _peerSlots.Keys)
        {
            if (targetPeerId != peerId)
                SendCustomization(targetPeerId, playerSlot, look);
        }

        if (IsHost && GodotObject.IsInstanceValid(CharacterCustomization.Instance))
            SendCustomization(peerId, 1, CharacterCustomization.Instance.LocalLook);
        else if (IsDedicatedServer)
        {
            foreach (var pair in _peerLooks)
            {
                if (pair.Key != peerId && _peerSlots.TryGetValue(pair.Key, out var remoteSlot))
                    SendCustomization(peerId, remoteSlot, pair.Value);
            }
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 2)]
    private void ReceiveCustomization(
        int playerSlot,
        float bodyR,
        float bodyG,
        float bodyB,
        float eyeR,
        float eyeG,
        float eyeB,
        int eyeShape,
        int eyeFollowLevel)
    {
        if (!IsClient || playerSlot == LocalPlayerSlot ||
            !GodotObject.IsInstanceValid(CharacterCustomization.Instance))
            return;

        CharacterCustomization.Instance.SetRemote(CharacterCustomization.FromNetwork(
            bodyR, bodyG, bodyB,
            eyeR, eyeG, eyeB,
            eyeShape, eyeFollowLevel));
    }

    private void SendCustomization(long peerId, int playerSlot, CharacterLook look)
    {
        look = look.Sanitized();
        RpcId(peerId, MethodName.ReceiveCustomization,
            playerSlot,
            look.BodyColor.R, look.BodyColor.G, look.BodyColor.B,
            look.EyeColor.R, look.EyeColor.G, look.EyeColor.B,
            (int)look.EyeShape, look.EyeFollowLevel);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered, TransferChannel = 0)]
    private void SubmitInput(byte[] payload)
    {
        if (!IsAuthority || _phase != LobbyPhase.InGame || payload.Length > 512 ||
            !NetworkProtocol.TryDecodeInputPacket(payload, out var commands))
            return;
        var peerId = Multiplayer.GetRemoteSenderId();
        if (!_peerSlots.TryGetValue(peerId, out var slot))
            return;
        _lastInputSequence.TryGetValue(peerId, out var lastSequence);
        _lastClientTick.TryGetValue(peerId, out var lastTick);
        foreach (var command in commands)
        {
            if (!NetworkProtocol.IsNewer(command.Sequence, lastSequence))
                continue;
            var advance = unchecked(command.Sequence - lastSequence);
            var tickAdvance = unchecked(command.ClientTick - lastTick);
            if ((lastSequence != 0 && advance > 120) ||
                (lastTick != 0 && (!NetworkProtocol.IsNewer(command.ClientTick, lastTick) || tickAdvance > 120)))
                continue;
            lastSequence = command.Sequence;
            lastTick = command.ClientTick;
            RemoteInputReceived?.Invoke(slot, command with { LatencyCompensation = GetOneWayLatency(peerId) });
        }
        _lastInputSequence[peerId] = lastSequence;
        _lastClientTick[peerId] = lastTick;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered, TransferChannel = 1)]
    private void ReceiveSnapshot(byte[] payload)
    {
        if (IsClient && _phase != LobbyPhase.Lobby && payload.Length is > 0 and <= 65536)
            SnapshotReceived?.Invoke(payload);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 2)]
    private void ReceiveAssignedSlot(int slot, bool dedicated, int protocolVersion, int mapCatalogVersion)
    {
        if (!IsClient)
            return;
        if (protocolVersion != NetworkProtocol.ProtocolVersion || mapCatalogVersion != NetworkProtocol.MapCatalogVersion)
        {
            ReturnToLobby($"버전 불일치: protocol {protocolVersion}/{NetworkProtocol.ProtocolVersion}, map {mapCatalogVersion}/{NetworkProtocol.MapCatalogVersion}");
            return;
        }
        LocalPlayerSlot = Mathf.Clamp(slot, 1, 2);
        Mode = dedicated ? SessionMode.DedicatedClient : SessionMode.Client;
        ConnectionActive = true;
        SetStatus(dedicated ? $"Dedicated server 연결 완료 — PLAYER {LocalPlayerSlot}" : "호스트 연결 완료 — READY를 누르세요.");
        ShareLocalCustomization();
        if (_autoReady && !LocalReady)
            ToggleReady();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 2)]
    private void ReceiveLobbyState(int connectedPlayers, int readyMask, int phase)
    {
        if (!IsClient)
            return;
        ConnectedPlayers = Mathf.Clamp(connectedPlayers, 0, 2);
        _phase = (LobbyPhase)Mathf.Clamp(phase, 0, 2);
        var localBit = LocalPlayerSlot > 0 ? 1 << (LocalPlayerSlot - 1) : 0;
        var remoteBit = LocalPlayerSlot == 1 ? 2 : 1;
        LocalReady = (readyMask & localBit) != 0;
        RemoteReady = (readyMask & remoteBit) != 0;
        if (_phase != LobbyPhase.InGame)
            SetStatus(_phase == LobbyPhase.Rematch
                ? $"재대결 준비: {CountBits(readyMask)}/{ConnectedPlayers}"
                : $"플레이어: {ConnectedPlayers}/2, READY: {CountBits(readyMask)}/{ConnectedPlayers}");
        LobbyChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 2)]
    private void StartNetworkGame(int protocolVersion, int mapCatalogVersion)
    {
        if (protocolVersion != NetworkProtocol.ProtocolVersion || mapCatalogVersion != NetworkProtocol.MapCatalogVersion)
        {
            ReturnToLobby("서버와 게임 데이터 버전이 다릅니다.");
            return;
        }
        _phase = LobbyPhase.InGame;
        LocalReady = false;
        RemoteReady = false;
        _sceneTransitionQueued = false;
        GD.Print($"PAAKURI network game loading as {Mode}, slot {LocalPlayerSlot}");
        GetTree().ChangeSceneToFile("res://Scene/main.tscn");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 2)]
    private void StartRematch()
    {
        if (!IsClient)
            return;
        _phase = LobbyPhase.InGame;
        LocalReady = false;
        RemoteReady = false;
        RematchStarted?.Invoke();
        LobbyChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 2)]
    private void ReturnDedicatedClientToLobby(string reason)
    {
        if (!IsDedicatedClient)
            return;
        _phase = LobbyPhase.Lobby;
        LocalReady = false;
        RemoteReady = false;
        Status = reason;
        if (GodotObject.IsInstanceValid(CharacterCustomization.Instance))
            CharacterCustomization.Instance.ResetRemote();
        GetTree().ChangeSceneToFile("res://Scene/start.tscn");
        LobbyChanged?.Invoke();
    }

    private void TryStartOrRematch()
    {
        if (!IsAuthority || !AllPlayersReady())
            return;
        if (_phase == LobbyPhase.Rematch)
        {
            _phase = LobbyPhase.InGame;
            ResetReadyFlags();
            foreach (var peerId in _peerSlots.Keys)
                RpcId(peerId, MethodName.StartRematch);
            RematchStarted?.Invoke();
            BroadcastLobbyState();
            return;
        }
        if (_phase != LobbyPhase.Lobby || _sceneTransitionQueued)
            return;
        _sceneTransitionQueued = true;
        _phase = LobbyPhase.InGame;
        ResetReadyFlags();
        foreach (var peerId in _peerSlots.Keys)
            RpcId(peerId, MethodName.StartNetworkGame, NetworkProtocol.ProtocolVersion, NetworkProtocol.MapCatalogVersion);
        StartNetworkGame(NetworkProtocol.ProtocolVersion, NetworkProtocol.MapCatalogVersion);
    }

    private bool AllPlayersReady()
    {
        if (IsHost)
            return _peerSlots.Count == 1 && LocalReady && _peerReady.GetValueOrDefault(RemotePeerId);
        if (!IsDedicatedServer || _peerSlots.Count != DedicatedMaxClients)
            return false;
        foreach (var peerId in _peerSlots.Keys)
            if (!_peerReady.GetValueOrDefault(peerId))
                return false;
        return true;
    }

    private void ResetReadyFlags()
    {
        LocalReady = false;
        RemoteReady = false;
        foreach (var peerId in _peerSlots.Keys)
            _peerReady[peerId] = false;
    }

    private void BroadcastLobbyState()
    {
        var readyMask = IsHost && LocalReady ? 1 : 0;
        foreach (var pair in _peerSlots)
            if (_peerReady.GetValueOrDefault(pair.Key))
                readyMask |= 1 << (pair.Value - 1);
        ConnectedPlayers = IsHost ? 1 + _peerSlots.Count : _peerSlots.Count;
        if (IsHost && RemotePeerId != 0)
            RemoteReady = _peerReady.GetValueOrDefault(RemotePeerId);
        foreach (var peerId in _peerSlots.Keys)
            RpcId(peerId, MethodName.ReceiveLobbyState, ConnectedPlayers, readyMask, (int)_phase);
        if (IsDedicatedServer)
            SetStatus($"Dedicated server {HostRoomCode} — {ConnectedPlayers}/2 players");
        LobbyChanged?.Invoke();
    }

    private void OnPeerConnected(long peerId)
    {
        if (!IsAuthority)
            return;
        var slot = IsHost ? 2 : FindFreeSlot();
        if (slot == 0)
            return;
        _peerSlots[peerId] = slot;
        _peerReady[peerId] = false;
        _lastInputSequence[peerId] = 0;
        _lastClientTick[peerId] = 0;
        RemotePeerId = IsHost ? peerId : RemotePeerId == 0 ? peerId : RemotePeerId;
        ConnectionActive = true;
        GD.Print($"PAAKURI peer connected: {peerId}, slot {slot} ({Mode})");
        RpcId(peerId, MethodName.ReceiveAssignedSlot, slot, IsDedicatedServer, NetworkProtocol.ProtocolVersion, NetworkProtocol.MapCatalogVersion);
        if (IsHost && GodotObject.IsInstanceValid(CharacterCustomization.Instance))
            SendCustomization(peerId, 1, CharacterCustomization.Instance.LocalLook);
        else if (IsDedicatedServer)
        {
            foreach (var pair in _peerLooks)
            {
                if (_peerSlots.TryGetValue(pair.Key, out var remoteSlot))
                    SendCustomization(peerId, remoteSlot, pair.Value);
            }
        }
        BroadcastLobbyState();
        if (_autoReady && IsHost && !LocalReady)
            ToggleReady();
    }

    private void OnPeerDisconnected(long peerId)
    {
        if (!IsAuthority || !_peerSlots.Remove(peerId))
            return;
        _peerReady.Remove(peerId);
        _lastInputSequence.Remove(peerId);
        _lastClientTick.Remove(peerId);
        _peerLooks.Remove(peerId);
        if (IsHost)
        {
            ReturnToLobby("참가자가 연결을 종료했습니다.");
            return;
        }

        RemotePeerId = 0;
        foreach (var remainingPeerId in _peerSlots.Keys)
        {
            RemotePeerId = remainingPeerId;
            break;
        }
        var matchWasRunning = _phase != LobbyPhase.Lobby;
        _phase = LobbyPhase.Lobby;
        ResetReadyFlags();
        if (matchWasRunning)
        {
            foreach (var remainingPeerId in _peerSlots.Keys)
                RpcId(remainingPeerId, MethodName.ReturnDedicatedClientToLobby, "상대 플레이어 연결 종료 — 새 플레이어를 기다립니다.");
            GetTree().ChangeSceneToFile("res://Scene/dedicated_server.tscn");
        }
        BroadcastLobbyState();
    }

    private void OnConnectedToServer()
    {
        if (!IsClient)
            return;
        ConnectionActive = true;
        RemotePeerId = 1;
        SetStatus("서버 연결 완료 — 슬롯 배정 대기 중...");
    }

    private void OnConnectionFailed() => ReturnToLobby("서버 연결에 실패했습니다.");
    private void OnServerDisconnected() => ReturnToLobby("서버 연결이 종료되었습니다.");

    private int FindFreeSlot()
    {
        for (var slot = 1; slot <= 2; slot++)
            if (!_peerSlots.ContainsValue(slot))
                return slot;
        return 0;
    }

    private float GetOneWayLatency(long peerId)
    {
        if (Multiplayer.MultiplayerPeer is not ENetMultiplayerPeer enet)
            return 0.0f;
        var peer = enet.GetPeer((int)peerId);
        if (peer is null)
            return 0.0f;
        var roundTripMilliseconds = peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime);
        return Mathf.Clamp((float)roundTripMilliseconds / 2000.0f, 0.0f, 0.1f);
    }

    private void SetStatus(string status)
    {
        Status = status;
        LobbyChanged?.Invoke();
    }

    private void RunProtocolSelfCheck()
    {
        var success = NetworkProtocol.SelfCheck(out var error);
        GD.Print(success ? "PAAKURI protocol self-check: PASS" : $"PAAKURI protocol self-check: FAIL — {error}");
        GetTree().Quit(success ? 0 : 1);
    }

    private static int CountBits(int value)
    {
        var count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }
        return count;
    }

    private static void ParseRoomCode(string roomCode, out string address, out int port)
    {
        var trimmed = roomCode.Trim();
        var separator = trimmed.LastIndexOf(':');
        address = separator > 0 ? trimmed[..separator] : trimmed;
        port = separator > 0 && int.TryParse(trimmed[(separator + 1)..], out var parsed) ? parsed : DefaultPort;
        if (string.IsNullOrWhiteSpace(address))
            address = "127.0.0.1";
        port = Mathf.Clamp(port, 1024, 65535);
    }

    private static string FindLanIpv4Address()
    {
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var properties = network.GetIPProperties();
                var hasIpv4Gateway = false;
                foreach (var gateway in properties.GatewayAddresses)
                {
                    if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))
                    {
                        hasIpv4Gateway = true;
                        break;
                    }
                }
                if (!hasIpv4Gateway)
                    continue;
                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                        return address.ToString();
                }
            }
        }
        catch (NetworkInformationException error)
        {
            GD.PushWarning($"LAN 주소 자동 감지 실패: {error.Message}");
        }

        foreach (var candidate in IP.GetLocalAddresses())
            if (IPAddress.TryParse(candidate, out var address) && address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                return candidate;
        return "127.0.0.1";
    }

    private static bool CanBindUdpPort(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
