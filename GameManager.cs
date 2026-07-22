using System.Collections.Generic;
using System.IO;
using Godot;

public partial class GameManager : Node2D
{
	private static readonly PackedScene BulletScene = GD.Load<PackedScene>("res://Scene/bullet.tscn");
	private const float CountdownDuration = 3.0f;
	private const float RoundEndDuration = 3.0f;
	private const int WinsRequired = 2;
	private const float SnapshotInterval = 1.0f / 30.0f;
	private const int SnapshotVersion = 2;

	private enum GameState { Countdown, Playing, RoundEnd, MatchEnd, Result }

	private readonly record struct MapDefinition(string Path, Vector2 PlayerOneSpawn, Vector2 PlayerTwoSpawn);

	private static readonly MapDefinition[] Maps =
	{
		new("res://resources/map_a.png", new Vector2(300, 260), new Vector2(1620, 260)),
		new("res://resources/map_b.png", new Vector2(170, 300), new Vector2(1750, 300)),
		new("res://resources/map_c.png", new Vector2(170, 300), new Vector2(1750, 300)),
		new("res://resources/map_d.png", new Vector2(300, 250), new Vector2(1620, 250)),
		new("res://resources/map_e.png", new Vector2(300, 250), new Vector2(1620, 250)),
		new("res://resources/map_f.png", new Vector2(300, 300), new Vector2(1620, 300)),
		new("res://resources/map_g.png", new Vector2(300, 300), new Vector2(1620, 300))
	};

	private static readonly (Color Background, Color Map)[] RoundPalettes =
	{
		(new Color(0.02f, 0.07f, 0.16f), new Color(0.25f, 1.35f, 1.8f)),
		(new Color(0.09f, 0.03f, 0.17f), new Color(1.8f, 0.35f, 1.3f)),
		(new Color(0.02f, 0.12f, 0.13f), new Color(0.55f, 1.8f, 0.75f)),
		(new Color(0.16f, 0.04f, 0.08f), new Color(1.8f, 0.9f, 0.25f)),
		(new Color(0.02f, 0.04f, 0.17f), new Color(0.45f, 0.75f, 1.8f)),
		(new Color(0.15f, 0.02f, 0.02f), new Color(1.8f, 0.25f, 0.3f)),
		(new Color(0.13f, 0.1f, 0.01f), new Color(1.8f, 1.55f, 0.2f)),
		(new Color(0.07f, 0.02f, 0.14f), new Color(1.15f, 0.45f, 1.8f)),
		(new Color(0.01f, 0.12f, 0.09f), new Color(0.3f, 1.8f, 1.25f)),
		(new Color(0.16f, 0.05f, 0.04f), new Color(1.8f, 0.55f, 0.4f)),
		(new Color(0.03f, 0.03f, 0.15f), new Color(0.7f, 0.45f, 1.8f)),
		(new Color(0.01f, 0.1f, 0.15f), new Color(0.25f, 1.7f, 1.6f))
	};

	private Player _playerOne = null!;
	private Player _playerTwo = null!;
	private StaticBody2D _arena = null!;
	private ColorRect _background = null!;
	private Sprite2D _mapVisual = null!;
	private Label _scoreLabel = null!;
	private Label _messageLabel = null!;
	private GameOverPanel _resultPanel = null!;

	private GameState _state;
	private float _stateTime;
	private float _fightMessageTime;
	private int _playerOneWins;
	private int _playerTwoWins;
	private int _roundPaletteIndex;
	private bool _deathResolutionQueued;
	private NetworkSession _session = null!;
	private float _snapshotTime;
	private int _currentMapIndex = -1;
	private bool _clientResultShown;
	private readonly Dictionary<int, Bullet> _replicaBullets = new();

	public override void _Ready()
	{
		_playerOne = GetNode<Player>("Player1");
		_playerTwo = GetNode<Player>("Player2");
		_arena = GetNode<StaticBody2D>("Arena");
		_background = GetNode<ColorRect>("Background");
		_mapVisual = GetNode<Sprite2D>("Arena/MapVisual");
		_scoreLabel = GetNode<Label>("HUD/Score");
		_messageLabel = GetNode<Label>("HUD/Message");
		_resultPanel = GetNode<GameOverPanel>("GameOverLayer/GameOverPanel");
		_resultPanel.RestartRequested += OnRestartRequested;
		_session = NetworkSession.Instance;
		ApplyCharacterLooks();
		GD.Print($"PAAKURI GameManager ready: {_session.Mode}");

		GetViewport().UseHdr2D = true;
		AddChild(new WorldEnvironment
		{
			Environment = new Godot.Environment
			{
				GlowEnabled = true,
				GlowIntensity = 1.25f,
				GlowHdrThreshold = 1.1f
			}
		});

		_playerOne.SetTarget(_playerTwo);
		_playerTwo.SetTarget(_playerOne);
		_playerOne.Died += OnPlayerDied;
		_playerTwo.Died += OnPlayerDied;
		if (_session.IsHost)
		{
			_playerTwo.IsBot = false;
			_playerTwo.UsesRemoteInput = true;
			_session.RemoteInputReceived += OnRemoteInputReceived;
		}
		else if (_session.IsClient)
		{
			_playerTwo.IsBot = false;
			_playerOne.IsNetworkReplica = true;
			_playerTwo.IsNetworkReplica = true;
			_session.SnapshotReceived += OnSnapshotReceived;
		}

		if (!_session.IsClient)
			StartMatch();
	}

	private void ApplyCharacterLooks()
	{
		var customization = CharacterCustomization.Instance;
		if (_session.IsHost)
		{
			_playerOne.ApplyCustomization(customization.LocalLook);
			_playerTwo.ApplyCustomization(customization.RemoteLook);
			return;
		}
		if (_session.IsClient)
		{
			_playerOne.ApplyCustomization(customization.RemoteLook);
			_playerTwo.ApplyCustomization(customization.LocalLook);
			return;
		}

		_playerOne.ApplyCustomization(customization.LocalLook);
		_playerTwo.ApplyCustomization(CharacterCustomization.DefaultOpponentLook);
	}

	public override void _ExitTree()
	{
		if (_session is null)
			return;
		_session.RemoteInputReceived -= OnRemoteInputReceived;
		_session.SnapshotReceived -= OnSnapshotReceived;
		_resultPanel.RestartRequested -= OnRestartRequested;
	}

	public override void _Process(double deltaValue)
	{
		var delta = (float)deltaValue;
		if (Input.IsActionJustPressed("open_menu"))
		{
			if (_session.IsNetworkGame)
				_session.ReturnToLobby("방에서 나왔습니다.");
			else
				GetTree().ChangeSceneToFile("res://Scene/start.tscn");
			return;
		}
		if (Input.IsActionJustPressed("restart_match") && !_session.IsClient)
			StartMatch();

		if (_session.IsClient)
		{
			_session.SendInput(_playerTwo.CaptureLocalCommand());
			UpdateHud();
			return;
		}

		switch (_state)
		{
			case GameState.Countdown:
				TickCountdown(delta);
				break;
			case GameState.Playing:
				TickPlaying(delta);
				break;
			case GameState.RoundEnd:
				_stateTime -= delta;
				if (_stateTime <= 0.0f)
					FinishRoundTransition();
				break;
		}
		UpdateHud();
		if (_session.IsHost)
		{
			_snapshotTime -= delta;
			if (_snapshotTime <= 0.0f)
			{
				_snapshotTime = SnapshotInterval;
				_session.BroadcastSnapshot(BuildSnapshot());
			}
		}
	}

	private void StartMatch()
	{
		_playerOneWins = 0;
		_playerTwoWins = 0;
		_resultPanel.Hide();
		BeginRound();
	}

	private void BeginRound()
	{
		ClearBullets();
		var palette = RoundPalettes[_roundPaletteIndex++ % RoundPalettes.Length];
		_background.Color = palette.Background;
		_mapVisual.Modulate = palette.Map;
		_currentMapIndex = (int)(GD.Randi() % Maps.Length);
		var map = Maps[_currentMapIndex];
		LoadMap(map);
		_playerOne.ResetForRound(map.PlayerOneSpawn);
		_playerTwo.ResetForRound(map.PlayerTwoSpawn);
		SetCombatEnabled(false);
		_state = GameState.Countdown;
		_stateTime = CountdownDuration;
		_deathResolutionQueued = false;
		_messageLabel.Text = "3";
		UpdateHud();
	}

	private void TickCountdown(float delta)
	{
		_stateTime -= delta;
		if (_stateTime <= 0.0f)
		{
			_state = GameState.Playing;
			_fightMessageTime = 0.55f;
			_messageLabel.Text = "FIGHT!";
			SetCombatEnabled(true);
			return;
		}
		_messageLabel.Text = Mathf.CeilToInt(_stateTime).ToString();
	}

	private void TickPlaying(float delta)
	{
		if (_fightMessageTime > 0.0f)
		{
			_fightMessageTime -= delta;
			if (_fightMessageTime <= 0.0f)
				_messageLabel.Text = string.Empty;
		}

	}

	private void OnPlayerDied(int playerId)
	{
		if (_state != GameState.Playing || _deathResolutionQueued)
			return;
		_deathResolutionQueued = true;
		Callable.From(ResolveDeaths).CallDeferred();
	}

	private void ResolveDeaths()
	{
		_deathResolutionQueued = false;
		if (_state != GameState.Playing)
			return;
		if (!_playerOne.IsAlive && !_playerTwo.IsAlive)
			EndRound(0);
		else if (!_playerOne.IsAlive)
			EndRound(2);
		else if (!_playerTwo.IsAlive)
			EndRound(1);
	}

	private void EndRound(int winnerId)
	{
		if (_state != GameState.Playing)
			return;
		_state = GameState.RoundEnd;
		_stateTime = RoundEndDuration;
		SetCombatEnabled(false);
		ClearBullets();
		if (winnerId == 1)
			_playerOneWins++;
		else if (winnerId == 2)
			_playerTwoWins++;
		_messageLabel.Text = winnerId == 0 ? "DRAW - ROUND REPLAY" : $"PLAYER {winnerId} WINS ROUND";
		GD.Print($"Round ended. Winner: {(winnerId == 0 ? "draw" : winnerId)}, score: {_playerOneWins}-{_playerTwoWins}");
	}

	private void FinishRoundTransition()
	{
		if (_playerOneWins < WinsRequired && _playerTwoWins < WinsRequired)
		{
			BeginRound();
			return;
		}
		_state = GameState.MatchEnd;
		var winner = _playerOneWins >= WinsRequired ? 1 : 2;
		_messageLabel.Text = string.Empty;
		_resultPanel.ShowResult(winner, _playerOneWins, _playerTwoWins);
		_state = GameState.Result;
	}

	private void SetCombatEnabled(bool enabled)
	{
		_playerOne.InputEnabled = enabled;
		_playerTwo.InputEnabled = enabled;
		_playerOne.CombatEnabled = enabled;
		_playerTwo.CombatEnabled = enabled;
	}

	private void LoadMap(MapDefinition map)
	{
		foreach (var child in _arena.GetChildren())
		{
			if (child is CollisionPolygon2D)
			{
				_arena.RemoveChild(child);
				child.QueueFree();
			}
		}

		var texture = GD.Load<Texture2D>(map.Path);
		_mapVisual.Texture = texture;
		var imageSize = texture.GetSize();
		var scale = Mathf.Min(1920.0f / imageSize.X, 1080.0f / imageSize.Y);
		_mapVisual.Position = new Vector2(960.0f, 540.0f);
		_mapVisual.Scale = Vector2.One * scale;

		using var bitmap = new Bitmap();
		bitmap.CreateFromImageAlpha(texture.GetImage(), 0.1f);
		var rect = new Rect2I(Vector2I.Zero, bitmap.GetSize());
		foreach (var polygon in bitmap.OpaqueToPolygons(rect, 2.0f))
		{
			var collision = new CollisionPolygon2D
			{
				Position = _mapVisual.Position - imageSize * scale * 0.5f,
				Scale = Vector2.One * scale,
				Polygon = polygon
			};
			_arena.AddChild(collision);
		}
	}

	private void ClearBullets()
	{
		foreach (var node in GetTree().GetNodesInGroup("bullets"))
			node.QueueFree();
		_replicaBullets.Clear();
	}

	private void OnRemoteInputReceived(PlayerCommand command) => _playerTwo.SetRemoteCommand(command);

	private void OnRestartRequested()
	{
		if (!_session.IsClient)
			StartMatch();
	}

	private byte[] BuildSnapshot()
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		writer.Write(SnapshotVersion);
		writer.Write((int)_state);
		writer.Write(_stateTime);
		writer.Write(_currentMapIndex);
		writer.Write(_playerOneWins);
		writer.Write(_playerTwoWins);
		writer.Write(_messageLabel.Text ?? string.Empty);
		WritePlayer(writer, _playerOne);
		WritePlayer(writer, _playerTwo);

		var bullets = GetTree().GetNodesInGroup("bullets");
		var countPosition = stream.Position;
		writer.Write(0);
		var count = 0;
		foreach (var node in bullets)
		{
			if (node is not Bullet bullet || bullet.IsReplica || !GodotObject.IsInstanceValid(bullet))
				continue;
			writer.Write(bullet.NetworkId);
			WriteVector(writer, bullet.GlobalPosition);
			WriteVector(writer, bullet.LinearVelocity);
			writer.Write(bullet.OwnerPlayerId);
			count++;
		}
		var endPosition = stream.Position;
		stream.Position = countPosition;
		writer.Write(count);
		stream.Position = endPosition;
		return stream.ToArray();
	}

	private static void WritePlayer(BinaryWriter writer, Player player)
	{
		WriteVector(writer, player.GlobalPosition);
		WriteVector(writer, player.Velocity);
		WriteVector(writer, player.AimDirection);
		writer.Write(player.Health);
		writer.Write(player.Ammo);
		writer.Write(player.ReloadSeconds);
		writer.Write(player.ParryCooldownSeconds);
		writer.Write(player.IsAlive);
		writer.Write(player.OnFloor);
		writer.Write(player.OnWall);
	}

	private static void WriteVector(BinaryWriter writer, Vector2 value)
	{
		writer.Write(value.X);
		writer.Write(value.Y);
	}

	private void OnSnapshotReceived(byte[] payload)
	{
		try
		{
			using var stream = new MemoryStream(payload, false);
			using var reader = new BinaryReader(stream);
			if (reader.ReadInt32() != SnapshotVersion)
				return;
			_state = (GameState)reader.ReadInt32();
			_stateTime = reader.ReadSingle();
			var mapIndex = reader.ReadInt32();
			_playerOneWins = reader.ReadInt32();
			_playerTwoWins = reader.ReadInt32();
			_messageLabel.Text = reader.ReadString();
			if (mapIndex >= 0 && mapIndex < Maps.Length && mapIndex != _currentMapIndex)
			{
				_currentMapIndex = mapIndex;
				LoadMap(Maps[mapIndex]);
			}
			ReadPlayer(reader, _playerOne);
			ReadPlayer(reader, _playerTwo);
			ReadBullets(reader);

			if (_state == GameState.Result && !_clientResultShown)
			{
				_clientResultShown = true;
				var winner = _playerOneWins >= WinsRequired ? 1 : 2;
				_resultPanel.ShowResult(winner, _playerOneWins, _playerTwoWins);
			}
			else if (_state != GameState.Result && _clientResultShown)
			{
				_clientResultShown = false;
				_resultPanel.Hide();
			}
		}
		catch (EndOfStreamException error)
		{
			GD.PushWarning($"잘린 네트워크 스냅샷을 무시했습니다: {error.Message}");
		}
	}

	private static void ReadPlayer(BinaryReader reader, Player player)
	{
		var position = ReadVector(reader);
		var velocity = ReadVector(reader);
		var aim = ReadVector(reader);
		var health = reader.ReadInt32();
		var ammo = reader.ReadInt32();
		var reload = reader.ReadSingle();
		var parryCooldown = reader.ReadSingle();
		var alive = reader.ReadBoolean();
		var onFloor = reader.ReadBoolean();
		var onWall = reader.ReadBoolean();
		player.ApplyNetworkState(position, velocity, aim, health, ammo, reload, parryCooldown, alive, onFloor, onWall);
	}

	private void ReadBullets(BinaryReader reader)
	{
		var liveIds = new HashSet<int>();
		var count = Mathf.Clamp(reader.ReadInt32(), 0, 64);
		for (var index = 0; index < count; index++)
		{
			var id = reader.ReadInt32();
			var position = ReadVector(reader);
			var velocity = ReadVector(reader);
			var ownerId = reader.ReadInt32();
			liveIds.Add(id);
			var owner = ownerId == 1 ? _playerOne : ownerId == 2 ? _playerTwo : null;
			if (_replicaBullets.TryGetValue(id, out var existing) && GodotObject.IsInstanceValid(existing))
			{
				existing.ApplyReplicaState(position, velocity, owner);
				continue;
			}
			var bullet = BulletScene.Instantiate<Bullet>();
			AddChild(bullet, true);
			bullet.ConfigureReplica(id, position, velocity, owner);
			_replicaBullets[id] = bullet;
		}

		var removed = new List<int>();
		foreach (var pair in _replicaBullets)
		{
			if (liveIds.Contains(pair.Key))
				continue;
			if (GodotObject.IsInstanceValid(pair.Value))
				pair.Value.QueueFree();
			removed.Add(pair.Key);
		}
		foreach (var id in removed)
			_replicaBullets.Remove(id);
	}

	private static Vector2 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle());

	private void UpdateHud()
	{
		_scoreLabel.Text = $"{_playerOneWins} : {_playerTwoWins}";
	}
}
