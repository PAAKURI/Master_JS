using Godot;

public partial class CustomizationPreview : SubViewportContainer
{
	private CharacterLook _look = CharacterCustomization.DefaultLocalLook;
	private SubViewport _previewViewport = null!;
	private Player _player = null!;
	private double _phase;

	public CharacterLook Look
	{
		get => _look;
		set
		{
			_look = value.Sanitized();
			if (GodotObject.IsInstanceValid(_player))
				_player.ApplyCustomization(_look);
		}
	}

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		_previewViewport = GetNode<SubViewport>("PreviewViewport");
		_player = _previewViewport.GetNode<Player>("Player");
		_player.ApplyCustomization(_look);
		SetProcess(true);
		Callable.From(UpdatePlayerPosition).CallDeferred();
	}

	public override void _Process(double deltaValue)
	{
		_phase += Mathf.Min((float)deltaValue, 1.0f / 30.0f) * 1.7f;
		UpdatePlayerPosition();
	}

	private void UpdatePlayerPosition()
	{
		if (!GodotObject.IsInstanceValid(_player) || !GodotObject.IsInstanceValid(_previewViewport))
			return;

		var center = new Vector2(_previewViewport.Size.X, _previewViewport.Size.Y) * 0.5f;
		_player.Position = center + new Vector2(
			Mathf.Sin((float)_phase) * 34.0f,
			Mathf.Sin((float)_phase * 2.0f) * 3.0f + 14.0f);
	}
}
