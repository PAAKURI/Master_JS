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
			() => GetTree().ChangeSceneToFile("res://Scene/start.tscn");
		GetNode<Button>("Panel/Content/QuitButton").Pressed += () => GetTree().Quit();
	}

	public void ShowResult(int winnerId, int playerOneWins, int playerTwoWins)
	{
		_title.Text = $"PLAYER {winnerId} WINS";
		_score.Text = $"{playerOneWins}  :  {playerTwoWins}";
		_restartButton.Disabled = NetworkSession.Instance.IsClient;
		Show();
		_restartButton.GrabFocus();
	}
}
