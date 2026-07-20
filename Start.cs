using Godot;

public partial class Start : Control
{
    public override void _Ready()
    {
        GetNode<Button>("Content/StartButton").Pressed += OnStartPressed;
    }

    private void OnStartPressed()
    {
        GetTree().ChangeSceneToFile("res://Scene/main.tscn");
    }
}
