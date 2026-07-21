using Godot;

public partial class Start : Control
{
    public override void _Ready()
    {
        GetNode<Button>("Content/StartButton").Pressed += OnStartPressed;
        GetNode<Button>("Content/QuitButton").Pressed += () => GetTree().Quit();
    }

    private void OnStartPressed()
    {
        GetTree().ChangeSceneToFile("res://Scene/main.tscn");
    }
}
