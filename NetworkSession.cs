using System;
using Godot;

public partial class NetworkSession : Node
{
    public enum SessionMode { OfflineAi, Host, Client }

    public const int DefaultPort = 24872;
    private const int MaxClients = 1;

    public static NetworkSession Instance { get; private set; } = null!;

    public SessionMode Mode { get; private set; } = SessionMode.OfflineAi;
    public bool IsNetworkGame => Mode != SessionMode.OfflineAi;
    public bool IsHost => Mode == SessionMode.Host;
    public bool IsClient => Mode == SessionMode.Client;
    public bool ConnectionActive { get; private set; }
    public bool LocalReady { get; private set; }
    public bool RemoteReady { get; private set; }
    public long RemotePeerId { get; private set; }
    public string Status { get; private set; } = "AI 대전을 선택하거나 방을 생성하세요.";

    public event Action? LobbyChanged;
    public event Action<PlayerCommand>? RemoteInputReceived;
    public event Action<byte[]>? SnapshotReceived;
    public event Action<string>? ConnectionClosed;

    private bool _autoReady;

    public override void _Ready()
    {
        Instance = this;
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument == "--paakuri-auto-ready")
                _autoReady = true;
            else if (argument == "--paakuri-host")
                Host();
            else if (argument.StartsWith("--paakuri-join="))
                Join(argument["--paakuri-join=".Length..]);
            else if (argument == "--paakuri-ai")
                CallDeferred(MethodName.StartOfflineAi);
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
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateServer(port, MaxClients, 3);
        if (error != Error.Ok)
        {
            SetStatus($"방 생성 실패: {error}");
            return error;
        }

        Multiplayer.MultiplayerPeer = peer;
        Mode = SessionMode.Host;
        ConnectionActive = true;
        GD.Print($"PAAKURI host listening on UDP {port}");
        SetStatus($"방 코드: 127.0.0.1:{port} (LAN에서는 호스트 IP 사용) — 참가자 대기 중");
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
        GD.Print($"PAAKURI client connecting to {address}:{port}");
        SetStatus($"{address}:{port} 연결 중...");
        return Error.Ok;
    }

    public void ToggleReady()
    {
        if (!IsNetworkGame || !ConnectionActive || RemotePeerId == 0)
            return;
        LocalReady = !LocalReady;
        if (IsHost)
        {
            if (RemotePeerId != 0)
                RpcId(RemotePeerId, MethodName.ReceiveHostReady, LocalReady);
            TryStartNetworkGame();
            LobbyChanged?.Invoke();
            return;
        }
        RpcId(1, MethodName.SubmitReady, LocalReady);
        LobbyChanged?.Invoke();
    }

    public void SendInput(PlayerCommand command)
    {
        if (!IsClient || !ConnectionActive)
            return;
        var value = command.Sanitized();
        RpcId(1, MethodName.SubmitInput, value.Move, value.Jump, value.Down, value.Up, value.Shoot, value.Parry, value.Aim);
    }

    public void BroadcastSnapshot(byte[] payload)
    {
        if (IsHost && RemotePeerId != 0)
            RpcId(RemotePeerId, MethodName.ReceiveSnapshot, payload);
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
        ConnectionActive = false;
        LocalReady = false;
        RemoteReady = false;
        RemotePeerId = 0;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitReady(bool ready)
    {
        if (!IsHost || Multiplayer.GetRemoteSenderId() != RemotePeerId)
            return;
        RemoteReady = ready;
        RpcId(RemotePeerId, MethodName.ReceiveHostReady, LocalReady);
        LobbyChanged?.Invoke();
        TryStartNetworkGame();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveHostReady(bool ready)
    {
        if (!IsClient)
            return;
        RemoteReady = ready;
        LobbyChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered, TransferChannel = 0)]
    private void SubmitInput(float move, bool jump, bool down, bool up, bool shoot, bool parry, Vector2 aim)
    {
        if (!IsHost || Multiplayer.GetRemoteSenderId() != RemotePeerId)
            return;
        RemoteInputReceived?.Invoke(new PlayerCommand(move, jump, down, up, shoot, parry, aim).Sanitized());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered, TransferChannel = 1)]
    private void ReceiveSnapshot(byte[] payload)
    {
        if (IsClient)
            SnapshotReceived?.Invoke(payload);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 2)]
    private void StartNetworkGame()
    {
        GD.Print($"PAAKURI network game loading as {Mode}");
        GetTree().ChangeSceneToFile("res://Scene/main.tscn");
    }

    private void TryStartNetworkGame()
    {
        if (!IsHost || RemotePeerId == 0 || !LocalReady || !RemoteReady)
            return;
        Rpc(MethodName.StartNetworkGame);
        StartNetworkGame();
    }

    private void OnPeerConnected(long peerId)
    {
        RemotePeerId = peerId;
        ConnectionActive = true;
        GD.Print($"PAAKURI peer connected: {peerId} ({Mode})");
        SetStatus(IsHost ? $"플레이어 {peerId} 참가 — 두 플레이어가 READY를 누르세요." : "호스트와 연결됨 — READY를 누르세요.");
        if (_autoReady && !LocalReady)
            ToggleReady();
    }

    private void OnPeerDisconnected(long peerId)
    {
        if (peerId != RemotePeerId)
            return;
        ReturnToLobby(IsHost ? "참가자가 연결을 종료했습니다." : "호스트가 연결을 종료했습니다.");
    }

    private void OnConnectedToServer()
    {
        ConnectionActive = true;
        SetStatus("호스트와 연결됨 — READY를 누르세요.");
    }

    private void OnConnectionFailed() => ReturnToLobby("호스트 연결에 실패했습니다.");
    private void OnServerDisconnected() => ReturnToLobby("호스트 연결이 종료되었습니다.");

    private void SetStatus(string status)
    {
        Status = status;
        LobbyChanged?.Invoke();
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
}
