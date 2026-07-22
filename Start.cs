using Godot;

public partial class Start : Control
{
	private VBoxContainer _menu = null!;

	public override void _Ready()
	{
		_menu = GetNode<VBoxContainer>("Menu");
		GetNode<Button>("Menu/LocalButton").Pressed += NetworkSession.Instance.StartOfflineAi;
		GetNode<Button>("Menu/MultiButton").Pressed += () => GetTree().ChangeSceneToFile("res://Scene/multiplayer.tscn");
		GetNode<Button>("Menu/CustomButton").Pressed += () => GetTree().ChangeSceneToFile("res://Scene/customization.tscn");
		GetNode<Button>("Menu/QuitButton").Pressed += () => GetTree().Quit();

		Callable.From(PlayEntrance).CallDeferred();

		foreach (var argument in OS.GetCmdlineUserArgs())
		{
			if (argument == "--paakuri-customization-smoke")
				Callable.From(() => GetTree().ChangeSceneToFile("res://Scene/customization.tscn")).CallDeferred();
		}
	}

	private void PlayEntrance()
	{
		var target = _menu.Position;
		_menu.Position = target + new Vector2(0.0f, 42.0f);
		_menu.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
		var tween = CreateTween().SetParallel().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
		tween.TweenProperty(_menu, "position", target, 0.52f).SetDelay(0.08f);
		tween.TweenProperty(_menu, "modulate:a", 1.0f, 0.38f).SetDelay(0.08f);
	}
}
