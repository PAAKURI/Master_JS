using Godot;

public partial class Start : Control
{
    private LineEdit _roomCode = null!;
    private Button _readyButton = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        _roomCode = GetNode<LineEdit>("Content/RoomCode");
        _readyButton = GetNode<Button>("Content/ReadyButton");
        _status = GetNode<Label>("Content/Status");
        _roomCode.PlaceholderText = $"호스트 LAN 주소 (예: {NetworkSession.Instance.LocalLanAddress}:{NetworkSession.DefaultPort})";
        GetNode<Button>("Content/AiButton").Pressed += NetworkSession.Instance.StartOfflineAi;
        GetNode<Button>("Content/HostButton").Pressed += () => NetworkSession.Instance.Host();
        GetNode<Button>("Content/JoinButton").Pressed += () => NetworkSession.Instance.Join(_roomCode.Text);
        _readyButton.Pressed += NetworkSession.Instance.ToggleReady;
        GetNode<Button>("Content/QuitButton").Pressed += () => GetTree().Quit();
        NetworkSession.Instance.LobbyChanged += UpdateLobby;
        UpdateLobby();
    }

    public override void _ExitTree()
    {
        NetworkSession.Instance.LobbyChanged -= UpdateLobby;
    }

    private void UpdateLobby()
    {
        var session = NetworkSession.Instance;
        _status.Text = session.Status;
        if (session.IsHost && !string.IsNullOrEmpty(session.HostRoomCode))
            _roomCode.Text = session.HostRoomCode;
        _readyButton.Disabled = !session.IsNetworkGame || !session.ConnectionActive || session.RemotePeerId == 0;
        _readyButton.Text = session.LocalReady ? "READY 취소" : "READY";
    }
}
