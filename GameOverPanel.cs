using Godot;

public partial class GameOverPanel : Control
{
	public event System.Action? RestartRequested;
	private Button _restartButton = null!;
	private Label _title = null!;
	private Label _score = null!;

	public override void _Ready()
	{
		_restartButton = GetNode<Button>("Panel/Content/RestartButton");
		_title = GetNode<Label>("Panel/Content/Title");
		_score = GetNode<Label>("Panel/Content/Score");
		_restartButton.Pressed += () => RestartRequested?.Invoke();
		GetNode<Button>("Panel/Content/TitleButton").Pressed +=
			() =>
			{
				if (NetworkSession.Instance.IsNetworkGame)
					NetworkSession.Instance.ReturnToLobby("타이틀로 돌아왔습니다.");
				else
					GetTree().ChangeSceneToFile("res://Scene/start.tscn");
			};
		GetNode<Button>("Panel/Content/QuitButton").Pressed += () => GetTree().Quit();
		NetworkSession.Instance.LobbyChanged += UpdateRestartButton;
	}

	public override void _ExitTree()
	{
		NetworkSession.Instance.LobbyChanged -= UpdateRestartButton;
	}

	public void ShowResult(int winnerId,  int playerOneWins, int playerTwoWins)
	{
		_title.Text = $"PLAYER {winnerId} WINS";
		_score.Text = $"{playerOneWins}  :  {playerTwoWins}";
		UpdateRestartButton();
		Show();
		_restartButton.GrabFocus();
	}

	private void UpdateRestartButton()
	{
		var session = NetworkSession.Instance;
		_restartButton.Disabled = session.IsClient && !session.IsDedicatedClient;
		_restartButton.Text = session.IsDedicatedClient
			? session.LocalReady ? "REMATCH READY ✓" : "REMATCH READY"
			: "RESTART";
	}
}
