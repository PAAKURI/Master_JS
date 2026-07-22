using System;
using Godot;

public enum EyeShape
{
	Round,
	Oval,
	Diamond,
	Square
}

public readonly record struct CharacterLook(
	Color BodyColor,
	Color EyeColor,
	EyeShape EyeShape,
	int EyeFollowLevel)
{
	public const int MinimumEyeFollowLevel = 1;
	public const int MaximumEyeFollowLevel = 5;
	public const int DefaultEyeFollowLevel = 3;

	public float EyeSpring => EyeFollowLevel switch
	{
		1 => 24.0f,
		2 => 40.0f,
		3 => 58.0f,
		4 => 82.0f,
		_ => 112.0f
	};

	public float EyeDamping => EyeFollowLevel switch
	{
		1 => 5.2f,
		2 => 6.3f,
		3 => 7.5f,
		4 => 9.0f,
		_ => 10.8f
	};

	public float EyeChaos => 35.0f;

	public CharacterLook Sanitized()
	{
		return new CharacterLook(
			Opaque(BodyColor),
			Opaque(EyeColor),
			(EyeShape)Mathf.Clamp((int)EyeShape, 0, Enum.GetValues<EyeShape>().Length - 1),
			Mathf.Clamp(EyeFollowLevel, MinimumEyeFollowLevel, MaximumEyeFollowLevel));
	}

	private static Color Opaque(Color color) => new(
		Mathf.Clamp(color.R, 0.0f, 1.0f),
		Mathf.Clamp(color.G, 0.0f, 1.0f),
		Mathf.Clamp(color.B, 0.0f, 1.0f),
		1.0f);
}

public partial class CharacterCustomization : Node
{
	private const string SavePath = "user://character_customization.cfg";
	private const string SaveSection = "character";

	public static CharacterCustomization Instance { get; private set; } = null!;

	public static readonly CharacterLook DefaultLocalLook = new(
		new Color("2aa9ff"),
		new Color("111521"),
		EyeShape.Round,
		CharacterLook.DefaultEyeFollowLevel);

	public static readonly CharacterLook DefaultOpponentLook = new(
		new Color("ff3f62"),
		new Color("fff4d6"),
		EyeShape.Oval,
		CharacterLook.DefaultEyeFollowLevel);

	public CharacterLook LocalLook { get; private set; } = DefaultLocalLook;
	public CharacterLook RemoteLook { get; private set; } = DefaultOpponentLook;

	public event Action? LocalLookChanged;
	public event Action? RemoteLookChanged;

	public override void _Ready()
	{
		Instance = this;
		LoadLocal();
	}

	public void SetLocal(CharacterLook look, bool save = true)
	{
		LocalLook = look.Sanitized();
		if (save)
			SaveLocal();
		LocalLookChanged?.Invoke();
	}

	public void SetRemote(CharacterLook look)
	{
		RemoteLook = look.Sanitized();
		RemoteLookChanged?.Invoke();
	}

	public void ResetRemote() => SetRemote(DefaultOpponentLook);

	public static CharacterLook FromNetwork(
		float bodyR,
		float bodyG,
		float bodyB,
		float eyeR,
		float eyeG,
		float eyeB,
		int eyeShape,
		int eyeFollowLevel)
	{
		return new CharacterLook(
			new Color(bodyR, bodyG, bodyB),
			new Color(eyeR, eyeG, eyeB),
			(EyeShape)eyeShape,
			eyeFollowLevel).Sanitized();
	}

	public static CharacterLook FromNetworkAppearance(
		float bodyR,
		float bodyG,
		float bodyB,
		float eyeR,
		float eyeG,
		float eyeB,
		int eyeShape)
	{
		return FromNetwork(
			bodyR, bodyG, bodyB,
			eyeR, eyeG, eyeB,
			eyeShape, CharacterLook.DefaultEyeFollowLevel);
	}

	public static Vector2[] BuildEyePolygon(EyeShape shape, float radius = 7.0f)
	{
		return shape switch
		{
			EyeShape.Oval => BuildEllipse(radius * 0.72f, radius * 1.18f, 14),
			EyeShape.Diamond => new[]
			{
				new Vector2(0.0f, -radius * 1.2f),
				new Vector2(radius, 0.0f),
				new Vector2(0.0f, radius * 1.2f),
				new Vector2(-radius, 0.0f)
			},
			EyeShape.Square => new[]
			{
				new Vector2(-radius * 0.86f, -radius * 0.86f),
				new Vector2(radius * 0.86f, -radius * 0.86f),
				new Vector2(radius * 0.86f, radius * 0.86f),
				new Vector2(-radius * 0.86f, radius * 0.86f)
			},
			_ => BuildEllipse(radius, radius, 14)
		};
	}

	private static Vector2[] BuildEllipse(float radiusX, float radiusY, int points)
	{
		var polygon = new Vector2[points];
		for (var index = 0; index < points; index++)
		{
			var angle = Mathf.Tau * index / points;
			polygon[index] = new Vector2(Mathf.Cos(angle) * radiusX, Mathf.Sin(angle) * radiusY);
		}
		return polygon;
	}

	private void LoadLocal()
	{
		var config = new ConfigFile();
		if (config.Load(SavePath) != Error.Ok)
			return;

		var bodyColor = config.GetValue(SaveSection, "body_color", DefaultLocalLook.BodyColor).AsColor();
		var eyeColor = config.GetValue(SaveSection, "eye_color", DefaultLocalLook.EyeColor).AsColor();
		var eyeShape = config.GetValue(SaveSection, "eye_shape", (int)DefaultLocalLook.EyeShape).AsInt32();
		var eyeFollowLevel = config.GetValue(SaveSection, "eye_follow_level", CharacterLook.DefaultEyeFollowLevel).AsInt32();
		LocalLook = FromNetwork(
			bodyColor.R, bodyColor.G, bodyColor.B,
			eyeColor.R, eyeColor.G, eyeColor.B,
			eyeShape, eyeFollowLevel);
	}

	private void SaveLocal()
	{
		var config = new ConfigFile();
		config.SetValue(SaveSection, "body_color", LocalLook.BodyColor);
		config.SetValue(SaveSection, "eye_color", LocalLook.EyeColor);
		config.SetValue(SaveSection, "eye_shape", (int)LocalLook.EyeShape);
		config.SetValue(SaveSection, "eye_follow_level", LocalLook.EyeFollowLevel);
		var error = config.Save(SavePath);
		if (error != Error.Ok)
			GD.PushWarning($"캐릭터 커스터마이징 저장 실패: {error}");
	}
}
