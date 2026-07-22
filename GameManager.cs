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
    private const int SnapshotVersion = 5;

    private enum GameState { Countdown, Playing, RoundEnd, MatchEnd, Result }

    private readonly record struct MapDefinition(string Path, Vector2 PlayerOneSpawn, Vector2 PlayerTwoSpawn);
    private sealed record MapResource(Texture2D Texture, Vector2 ImageSize, Vector2[][] CollisionPolygons);

    private static readonly MapDefinition[] Maps =
    {
        new("res://resources/map_a.png", new Vector2(300, 260), new Vector2(1620, 260)),
        new("res://resources/map_c.png", new Vector2(170, 300), new Vector2(1750, 300)),
        new("res://resources/map_d.png", new Vector2(300, 250), new Vector2(1620, 250)),
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

    private static readonly Dictionary<string, MapResource> MapCache = new();

    private Player _playerOne = null!;
    private Player _playerTwo = null!;
    private StaticBody2D _arena = null!;
    private ColorRect _background = null!;
    private Sprite2D _mapVisual = null!;
    private Label _scoreLabel = null!;
    private Label _messageLabel = null!;
    private GameOverPanel _resultPanel = null!;
    private CameraShake _cameraShake = null!;

    private GameState _state;
    private float _stateTime;
    private float _fightMessageTime;
    private int _playerOneWins;
    private int _playerTwoWins;
    private bool _deathResolutionQueued;
    private NetworkSession _session = null!;
    private float _snapshotTime;
    private int _currentMapIndex = -1;
    private uint _roundId;
    private uint _lastAppliedRoundId;
    private uint _serverTick;
    private uint _lastCameraShakeSequence;
    private uint _bulletImpactSequence;
    private uint _lastBulletImpactSequence;
    private Vector2 _lastBulletImpactPosition;
    private Vector2 _lastBulletImpactNormal;
    private bool _clientResultShown;
    private bool _headless;
    private Player? _localPlayer;
    private readonly Dictionary<int, Bullet> _replicaBullets = new();

    public override void _Ready()
    {
        AddToGroup("network_effects");
        _playerOne = GetNode<Player>("Player1");
        _playerTwo = GetNode<Player>("Player2");
        _arena = GetNode<StaticBody2D>("Arena");
        _background = GetNode<ColorRect>("Background");
        _mapVisual = GetNode<Sprite2D>("Arena/MapVisual");
        _scoreLabel = GetNode<Label>("HUD/Score");
        _messageLabel = GetNode<Label>("HUD/Message");
        _resultPanel = GetNode<GameOverPanel>("GameOverLayer/GameOverPanel");
        _cameraShake = GetNode<CameraShake>("Camera2D");
        _resultPanel.RestartRequested += OnRestartRequested;
        _session = NetworkSession.Instance;
        _headless = DisplayServer.GetName() == "headless" || _session.IsDedicatedServer;
        GD.Print($"PAAKURI GameManager ready: {_session.Mode}");

        if (!_headless)
        {
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
        }
        else
        {
            _background.Visible = false;
            _mapVisual.Visible = false;
            GetNode<CanvasLayer>("HUD").Visible = false;
            GetNode<CanvasLayer>("DamageOverlayLayer").Visible = false;
            GetNode<CanvasLayer>("GameOverLayer").Visible = false;
            _playerOne.SetVisualsEnabled(false);
            _playerTwo.SetVisualsEnabled(false);
        }

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
        else if (_session.IsDedicatedServer)
        {
            _playerOne.IsBot = false;
            _playerTwo.IsBot = false;
            _playerOne.UsesRemoteInput = true;
            _playerTwo.UsesRemoteInput = true;
            _session.RemoteInputReceived += OnRemoteInputReceived;
        }
        else if (_session.IsClient)
        {
            _playerOne.IsBot = false;
            _playerTwo.IsBot = false;
            _localPlayer = _session.LocalPlayerSlot == 1 ? _playerOne : _playerTwo;
            var remotePlayer = _session.LocalPlayerSlot == 1 ? _playerTwo : _playerOne;
            _localPlayer.UsesRemoteInput = true;
            _localPlayer.IsPredictedLocal = true;
            remotePlayer.IsNetworkReplica = true;
            _session.SnapshotReceived += OnSnapshotReceived;
        }
        _session.RematchStarted += OnRematchStarted;

        if (!_session.IsClient)
            StartMatch();
    }

    public override void _ExitTree()
    {
        if (_session is null)
            return;
        _session.RemoteInputReceived -= OnRemoteInputReceived;
        _session.SnapshotReceived -= OnSnapshotReceived;
        _session.RematchStarted -= OnRematchStarted;
        _resultPanel.RestartRequested -= OnRestartRequested;
    }

    public override void _Process(double deltaValue)
    {
        var delta = (float)deltaValue;
        if (!_headless && Input.IsActionJustPressed("open_menu"))
        {
            if (_session.IsNetworkGame)
                _session.ReturnToLobby("방에서 나왔습니다.");
            else
                GetTree().ChangeSceneToFile("res://Scene/start.tscn");
            return;
        }
        if (!_headless && Input.IsActionJustPressed("restart_match") && !_session.IsClient)
            StartMatch();

        if (_session.IsClient)
        {
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
    }

    public override void _PhysicsProcess(double deltaValue)
    {
        var delta = (float)deltaValue;
        if (_session.IsClient && _localPlayer is not null)
        {
            var command = _session.SendInput(_localPlayer.CaptureLocalCommand());
            if (command.Sequence != 0)
                _localPlayer.EnqueueCommand(command);
            return;
        }
        if (_session.IsAuthority)
        {
            _serverTick++;
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
        _roundId++;
        if (!_headless)
            ApplyLocalPalette(_roundId);
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
        if (_session.IsDedicatedServer)
            _session.BeginRematchLobby();
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

        var resource = GetMapResource(map.Path);
        if (resource is null)
            return;
        var texture = resource.Texture;
        var imageSize = resource.ImageSize;
        var scale = Mathf.Min(1920.0f / imageSize.X, 1080.0f / imageSize.Y);
        _mapVisual.Position = new Vector2(960.0f, 540.0f);
        _mapVisual.Scale = Vector2.One * scale;
        if (!_headless)
            _mapVisual.Texture = texture;

        foreach (var polygon in resource.CollisionPolygons)
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

    private static MapResource? GetMapResource(string path)
    {
        if (MapCache.TryGetValue(path, out var cached))
            return cached;
        var texture = GD.Load<Texture2D>(path);
        if (texture is null)
        {
            GD.PushError($"Map texture load failed: {path}");
            return null;
        }
        using var bitmap = new Bitmap();
        bitmap.CreateFromImageAlpha(texture.GetImage(), 0.1f);
        var polygons = new List<Vector2[]>();
        var rect = new Rect2I(Vector2I.Zero, bitmap.GetSize());
        foreach (var polygon in bitmap.OpaqueToPolygons(rect, 2.0f))
            polygons.Add(polygon);
        var resource = new MapResource(texture, texture.GetSize(), polygons.ToArray());
        MapCache[path] = resource;
        return resource;
    }

    private void ApplyLocalPalette(uint roundId)
    {
        var peerSalt = _session.IsNetworkGame ? (uint)Multiplayer.GetUniqueId() : 0u;
        var index = (int)((roundId * 2654435761u ^ peerSalt * 2246822519u) % (uint)RoundPalettes.Length);
        var palette = RoundPalettes[index];
        _background.Color = palette.Background;
        _mapVisual.Modulate = palette.Map;
        _lastAppliedRoundId = roundId;
    }

    private void ClearBullets()
    {
        foreach (var node in GetTree().GetNodesInGroup("bullets"))
            node.QueueFree();
        _replicaBullets.Clear();
    }

    private void OnRemoteInputReceived(int playerSlot, PlayerCommand command)
    {
        if (playerSlot == 1)
            _playerOne.EnqueueCommand(command);
        else if (playerSlot == 2)
            _playerTwo.EnqueueCommand(command);
    }

    private void OnRestartRequested()
    {
        if (_session.IsDedicatedClient)
            _session.ToggleReady();
        else if (!_session.IsClient)
            StartMatch();
    }

    private void OnRematchStarted()
    {
        _clientResultShown = false;
        _resultPanel.Hide();
        if (_session.IsAuthority)
            StartMatch();
    }

    private byte[] BuildSnapshot()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(SnapshotVersion);
        writer.Write(_serverTick);
        writer.Write(_roundId);
        writer.Write((int)_state);
        writer.Write(_stateTime);
        writer.Write(_currentMapIndex);
        writer.Write(_playerOneWins);
        writer.Write(_playerTwoWins);
        writer.Write(_messageLabel.Text ?? string.Empty);
        writer.Write(_cameraShake.EventSequence);
        writer.Write(_cameraShake.CurrentStrength);
        writer.Write(_cameraShake.RemainingTime);
        writer.Write(_bulletImpactSequence);
        WriteVector(writer, _lastBulletImpactPosition);
        WriteVector(writer, _lastBulletImpactNormal);
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
            if (count >= 64)
                break;
        }
        var endPosition = stream.Position;
        stream.Position = countPosition;
        writer.Write(count);
        stream.Position = endPosition;
        return stream.ToArray();
    }

    private static void WritePlayer(BinaryWriter writer, Player player)
    {
        writer.Write(player.LastProcessedInputSequence);
        writer.Write(player.ShootSequence);
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
            var serverTick = reader.ReadUInt32();
            var roundId = reader.ReadUInt32();
            var roundChanged = roundId != _roundId;
            _state = (GameState)reader.ReadInt32();
            _stateTime = reader.ReadSingle();
            var mapIndex = reader.ReadInt32();
            _playerOneWins = reader.ReadInt32();
            _playerTwoWins = reader.ReadInt32();
            _messageLabel.Text = reader.ReadString();
            var cameraShakeSequence = reader.ReadUInt32();
            var cameraShakeStrength = reader.ReadSingle();
            var cameraShakeTime = reader.ReadSingle();
            if (cameraShakeSequence != _lastCameraShakeSequence)
            {
                _lastCameraShakeSequence = cameraShakeSequence;
                if (cameraShakeStrength > 0.0f && cameraShakeTime > 0.0f)
                    _cameraShake.ApplyNetworkShake(cameraShakeStrength, cameraShakeTime);
            }
            var bulletImpactSequence = reader.ReadUInt32();
            var bulletImpactPosition = ReadVector(reader);
            var bulletImpactNormal = ReadVector(reader);
            if (bulletImpactSequence != _lastBulletImpactSequence)
            {
                _lastBulletImpactSequence = bulletImpactSequence;
                if (bulletImpactSequence != 0 && bulletImpactPosition.IsFinite() && bulletImpactNormal.IsFinite() &&
                    bulletImpactNormal.LengthSquared() > 0.01f)
                {
                    Bullet.SpawnImpactEffect(this, bulletImpactPosition, bulletImpactNormal.Normalized());
                }
            }
            if (mapIndex >= 0 && mapIndex < Maps.Length && mapIndex != _currentMapIndex)
            {
                _currentMapIndex = mapIndex;
                LoadMap(Maps[mapIndex]);
            }
            if (roundId != _lastAppliedRoundId && !_headless)
                ApplyLocalPalette(roundId);
            _roundId = roundId;
            ReadPlayer(reader, serverTick, _playerOne, roundChanged);
            ReadPlayer(reader, serverTick, _playerTwo, roundChanged);
            ReadBullets(reader);
            var playing = _state == GameState.Playing;
            if (_localPlayer is not null)
            {
                _localPlayer.InputEnabled = playing;
                _localPlayer.CombatEnabled = false;
            }

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
        catch (IOException error)
        {
            GD.PushWarning($"잘린 네트워크 스냅샷을 무시했습니다: {error.Message}");
        }
    }

    private void ReadPlayer(BinaryReader reader, uint serverTick, Player player, bool resetForRound)
    {
        var acknowledgedSequence = reader.ReadUInt32();
        var shootSequence = reader.ReadUInt32();
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
        if (resetForRound)
            player.ResetForRound(position);
        player.ApplyNetworkShootSequence(shootSequence);
        if (player == _localPlayer)
        {
            _session.AcknowledgeInputs(acknowledgedSequence);
            player.ReconcileNetworkState(acknowledgedSequence, position, velocity, aim, health, ammo, reload, parryCooldown, alive, onFloor, onWall);
        }
        else
        {
            player.ApplyNetworkState(serverTick, position, velocity, aim, health, ammo, reload, parryCooldown, alive, onFloor, onWall);
        }
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

    public void record_bullet_impact(Vector2 position, Vector2 normal)
    {
        // ponytail: A snapshot carries only the newest cosmetic impact; use a small ring buffer only if simultaneous hits visibly disappear.
        _bulletImpactSequence++;
        _lastBulletImpactPosition = position;
        _lastBulletImpactNormal = normal;
    }

    private void UpdateHud()
    {
        _scoreLabel.Text = $"{_playerOneWins} : {_playerTwoWins}";
    }
}
