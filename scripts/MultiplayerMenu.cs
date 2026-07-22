using System.Collections.Generic;
using Godot;

public partial class MultiplayerMenu : Control
{
	private VBoxContainer _menu = null!;
	private LineEdit _localAddress = null!;
	private LineEdit _joinAddress = null!;
	private Button _readyButton = null!;
	private Label _status = null!;
	private readonly Dictionary<Button, Tween> _buttonTweens = new();

	public override void _Ready()
	{
		_menu = GetNode<VBoxContainer>("Menu");
		_localAddress = GetNode<LineEdit>("Menu/LocalAddress");
		_joinAddress = GetNode<LineEdit>("Menu/JoinAddress");
		_readyButton = GetNode<Button>("Menu/ReadyButton");
		_status = GetNode<Label>("Menu/Status");

		GetNode<Button>("Menu/HostButton").Pressed += HostGame;
		GetNode<Button>("Menu/JoinButton").Pressed += JoinGame;
		_readyButton.Pressed += NetworkSession.Instance.ToggleReady;
		GetNode<Button>("BackButton").Pressed += ReturnToStart;
		_joinAddress.TextSubmitted += _ => JoinGame();

		SetupButtonMotion();
		NetworkSession.Instance.LobbyChanged += UpdateLobby;
		UpdateLobby();
		Callable.From(PlayEntrance).CallDeferred();
	}

	public override void _ExitTree()
	{
		if (GodotObject.IsInstanceValid(NetworkSession.Instance))
			NetworkSession.Instance.LobbyChanged -= UpdateLobby;
	}

	public override void _UnhandledInput(InputEvent inputEvent)
	{
		if (!inputEvent.IsActionPressed("open_menu"))
			return;
		ReturnToStart();
		GetViewport().SetInputAsHandled();
	}

	private void HostGame()
	{
		NetworkSession.Instance.Host();
		UpdateLobby();
	}

	private void JoinGame()
	{
		NetworkSession.Instance.Join(_joinAddress.Text);
		UpdateLobby();
	}

	private void ReturnToStart()
	{
		NetworkSession.Instance.Disconnect();
		GetTree().ChangeSceneToFile("res://Scene/start.tscn");
	}

	private void UpdateLobby()
	{
		var session = NetworkSession.Instance;
		_localAddress.Text = session.IsHost && !string.IsNullOrWhiteSpace(session.HostRoomCode)
			? session.HostRoomCode
			: $"{session.LocalLanAddress}:{NetworkSession.DefaultPort}";
		_status.Text = session.Status;
		_readyButton.Disabled = !session.IsNetworkGame || !session.ConnectionActive || session.RemotePeerId == 0;
		_readyButton.Text = session.LocalReady ? "READY   ·   준비 취소" : "READY   ·   경기 준비";
	}

	private void SetupButtonMotion()
	{
		foreach (var node in FindChildren("*", "Button", true, false))
		{
			if (node is not Button button)
				continue;
			button.MouseDefaultCursorShape = CursorShape.PointingHand;
			button.Resized += () => button.PivotOffset = button.Size * 0.5f;
			button.MouseEntered += () => AnimateButton(button, new Vector2(1.02f, 1.02f), new Color(1.05f, 1.05f, 1.05f));
			button.MouseExited += () => AnimateButton(button, Vector2.One, Colors.White);
		}
	}

	private void AnimateButton(Button button, Vector2 scale, Color modulation)
	{
		if (button.Disabled)
			return;
		if (_buttonTweens.Remove(button, out var existing) && existing.IsValid())
			existing.Kill();
		var tween = CreateTween().SetParallel().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
		tween.TweenProperty(button, "scale", scale, 0.12f);
		tween.TweenProperty(button, "self_modulate", modulation, 0.12f);
		_buttonTweens[button] = tween;
	}

	private void PlayEntrance()
	{
		var target = _menu.Position;
		_menu.Position = target + new Vector2(0.0f, 34.0f);
		_menu.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
		var tween = CreateTween().SetParallel().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
		tween.TweenProperty(_menu, "position", target, 0.48f);
		tween.TweenProperty(_menu, "modulate:a", 1.0f, 0.34f);
	}
}
