using Godot;

public partial class CustomizationMenu : Control
{
	private CustomizationPreview _preview = null!;
	private ColorPickerButton _bodyColor = null!;
	private ColorPickerButton _eyeColor = null!;
	private OptionButton _eyeShape = null!;
	private HSlider _eyeFollow = null!;
	private Label _eyeFollowValue = null!;
	private CharacterLook _draft;
	private bool _syncing;

	public override void _Ready()
	{
		_preview = GetNode<CustomizationPreview>("CustomizationPanel/Content/Body/PreviewFrame/Preview");
		_bodyColor = GetNode<ColorPickerButton>("CustomizationPanel/Content/Body/SettingsFrame/Settings/BodyColor");
		_eyeColor = GetNode<ColorPickerButton>("CustomizationPanel/Content/Body/SettingsFrame/Settings/EyeColor");
		_eyeShape = GetNode<OptionButton>("CustomizationPanel/Content/Body/SettingsFrame/Settings/EyeShape");
		_eyeFollow = GetNode<HSlider>("CustomizationPanel/Content/Body/SettingsFrame/Settings/EyeFollowSlider");
		_eyeFollowValue = GetNode<Label>("CustomizationPanel/Content/Body/SettingsFrame/Settings/EyeFollowHeader/EyeFollowValue");

		SetupControls();
		SetControls(CharacterCustomization.Instance.LocalLook);
	}

	public override void _UnhandledInput(InputEvent inputEvent)
	{
		if (!inputEvent.IsActionPressed("open_menu"))
			return;
		ReturnToStart();
		GetViewport().SetInputAsHandled();
	}

	private void SetupControls()
	{
		_eyeShape.AddItem("동그란 눈", (int)EyeShape.Round);
		_eyeShape.AddItem("세로로 긴 눈", (int)EyeShape.Oval);
		_eyeShape.AddItem("다이아몬드 눈", (int)EyeShape.Diamond);
		_eyeShape.AddItem("네모난 눈", (int)EyeShape.Square);

		_bodyColor.ColorChanged += color =>
		{
			if (_syncing)
				return;
			_draft = _draft with { BodyColor = Opaque(color) };
			ApplyDraft();
		};
		_eyeColor.ColorChanged += color =>
		{
			if (_syncing)
				return;
			_draft = _draft with { EyeColor = Opaque(color) };
			ApplyDraft();
		};
		_eyeShape.ItemSelected += index =>
		{
			if (_syncing)
				return;
			_draft = _draft with { EyeShape = (EyeShape)_eyeShape.GetItemId((int)index) };
			ApplyDraft();
		};
		_eyeFollow.ValueChanged += value =>
		{
			if (_syncing)
				return;
			_draft = _draft with { EyeFollowLevel = Mathf.RoundToInt(value) };
			ApplyDraft();
		};

		GetNode<Button>("BackButton").Pressed += ReturnToStart;
		GetNode<Button>("CustomizationPanel/Content/Actions/DefaultsButton").Pressed += () => SetControls(CharacterCustomization.DefaultLocalLook);
		GetNode<Button>("CustomizationPanel/Content/Actions/CancelButton").Pressed += ReturnToStart;
		GetNode<Button>("CustomizationPanel/Content/Actions/SaveButton").Pressed += SaveAndReturn;
	}

	private void SetControls(CharacterLook look)
	{
		_syncing = true;
		_draft = look.Sanitized();
		_bodyColor.Color = _draft.BodyColor;
		_eyeColor.Color = _draft.EyeColor;
		SelectItemById(_eyeShape, (int)_draft.EyeShape);
		_eyeFollow.Value = _draft.EyeFollowLevel;
		_syncing = false;
		ApplyDraft();
	}

	private void ApplyDraft()
	{
		_draft = _draft.Sanitized();
		_preview.Look = _draft;
		var speedName = _draft.EyeFollowLevel switch
		{
			1 => "매우 느림",
			2 => "느림",
			3 => "보통",
			4 => "빠름",
			_ => "매우 빠름"
		};
		_eyeFollowValue.Text = $"{_draft.EyeFollowLevel}단계  ·  {speedName}";
	}

	private void SaveAndReturn()
	{
		CharacterCustomization.Instance.SetLocal(_draft);
		NetworkSession.Instance.ShareLocalCustomization();
		ReturnToStart();
	}

	private void ReturnToStart()
	{
		GetTree().ChangeSceneToFile("res://Scene/start.tscn");
	}

	private static void SelectItemById(OptionButton option, int id)
	{
		for (var index = 0; index < option.ItemCount; index++)
		{
			if (option.GetItemId(index) != id)
				continue;
			option.Select(index);
			return;
		}
	}

	private static Color Opaque(Color color) => new(color.R, color.G, color.B, 1.0f);
}
