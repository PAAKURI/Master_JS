using Godot;

public partial class GameOverPanel : Control
{
    private Button _restartButton = null!;

    public override void _Ready()
    {
        _restartButton = GetNode<Button>("Panel/Content/RestartButton");
        _restartButton.Pressed += () => GetTree().ReloadCurrentScene();
        GetNode<Button>("Panel/Content/TitleButton").Pressed +=
            () => GetTree().ChangeSceneToFile("res://Scene/start.tscn");
    }

    public void ShowGameOver()
    {
        Show();
        _restartButton.GrabFocus();
    }
}
