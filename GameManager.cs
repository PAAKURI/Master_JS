using Godot;

public partial class GameManager : Node2D
{
    private const float CountdownDuration = 3.0f;
    private const float RoundEndDuration = 3.0f;
    private const int WinsRequired = 2;

    private enum GameState { Countdown, Playing, RoundEnd, MatchEnd, Result }

    private readonly record struct MapDefinition(string Path, Vector2 PlayerOneSpawn, Vector2 PlayerTwoSpawn);

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
        StartMatch();
    }

    public override void _Process(double deltaValue)
    {
        var delta = (float)deltaValue;
        if (Input.IsActionJustPressed("open_menu"))
        {
            GetTree().ChangeSceneToFile("res://Scene/start.tscn");
            return;
        }
        if (Input.IsActionJustPressed("restart_match"))
            StartMatch();

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
        var map = Maps[(int)(GD.Randi() % Maps.Length)];
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
    }

    private void UpdateHud()
    {
        _scoreLabel.Text = $"{_playerOneWins} : {_playerTwoWins}";
    }
}
