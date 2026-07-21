using Godot;

public partial class GameManager : Node2D
{
    private const float RoundDuration = 60.0f;
    private const int WinsRequired = 2;
    private static readonly Vector2 PlayerOneSpawn = new(445.0f, 300.0f);
    private static readonly Vector2 PlayerTwoSpawn = new(1475.0f, 300.0f);

    private Player _playerOne = null!;
    private Player _playerTwo = null!;
    private Label _scoreLabel = null!;
    private Label _timerLabel = null!;
    private Label _messageLabel = null!;
    private Label _playerOneStatus = null!;
    private Label _playerTwoStatus = null!;
    private Label _helpLabel = null!;
    private float _roundTime;
    private int _playerOneWins;
    private int _playerTwoWins;
    private bool _roundActive;
    private bool _transitioning;
    private bool _deathResolutionQueued;

    public override void _Ready()
    {
        _playerOne = GetNode<Player>("Player1");
        _playerTwo = GetNode<Player>("Player2");
        _scoreLabel = GetNode<Label>("HUD/Score");
        _timerLabel = GetNode<Label>("HUD/Timer");
        _messageLabel = GetNode<Label>("HUD/Message");
        _playerOneStatus = GetNode<Label>("HUD/Player1Status");
        _playerTwoStatus = GetNode<Label>("HUD/Player2Status");
        _helpLabel = GetNode<Label>("HUD/Help");
        _playerOne.SetTarget(_playerTwo);
        _playerTwo.SetTarget(_playerOne);
        _playerOne.Died += OnPlayerDied;
        _playerTwo.Died += OnPlayerDied;
        _helpLabel.Text = "A/D 이동  W 벽 오르기  S 숙이기/빠른 낙하  Space 점프\n좌클릭 사격  우클릭 패링  R 재시작  ESC 시작 화면";
        StartMatch();
    }

    public override void _Process(double deltaValue)
    {
        if (Input.IsKeyPressed(Key.Escape))
            GetTree().ChangeSceneToFile("res://Scene/start.tscn");
        if (Input.IsKeyPressed(Key.R) && !_transitioning)
            StartMatch();

        if (_roundActive)
        {
            _roundTime = Mathf.Max(_roundTime - (float)deltaValue, 0.0f);
            if (_playerOne.GlobalPosition.Y > 1120.0f)
                _playerOne.Kill();
            if (_playerTwo.GlobalPosition.Y > 1120.0f)
                _playerTwo.Kill();
            if (_roundTime <= 0.0f)
                ResolveTimeout();
        }
        UpdateHud();
    }

    private async void StartMatch()
    {
        _transitioning = true;
        _roundActive = false;
        _playerOneWins = 0;
        _playerTwoWins = 0;
        await StartRound();
    }

    private async System.Threading.Tasks.Task StartRound()
    {
        ClearBullets();
        _playerOne.ResetForRound(PlayerOneSpawn);
        _playerTwo.ResetForRound(PlayerTwoSpawn);
        _roundTime = RoundDuration;
        _deathResolutionQueued = false;
        UpdateHud();
        for (var count = 3; count >= 1; count--)
        {
            _messageLabel.Text = count.ToString();
            await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        }
        _messageLabel.Text = "FIGHT!";
        _playerOne.InputEnabled = true;
        _playerTwo.InputEnabled = true;
        _roundActive = true;
        _transitioning = false;
        await ToSignal(GetTree().CreateTimer(0.55), SceneTreeTimer.SignalName.Timeout);
        if (_roundActive)
            _messageLabel.Text = string.Empty;
    }

    private void OnPlayerDied(int playerId)
    {
        if (!_roundActive || _transitioning || _deathResolutionQueued)
            return;
        _deathResolutionQueued = true;
        Callable.From(ResolveDeaths).CallDeferred();
    }

    private void ResolveDeaths()
    {
        _deathResolutionQueued = false;
        if (!_roundActive || _transitioning)
            return;
        if (!_playerOne.IsAlive && !_playerTwo.IsAlive)
        {
            EndRound(0);
            return;
        }
        if (!_playerOne.IsAlive)
            EndRound(2);
        else if (!_playerTwo.IsAlive)
            EndRound(1);
    }

    private void ResolveTimeout()
    {
        if (_playerOne.Health == _playerTwo.Health)
            EndRound(0);
        else
            EndRound(_playerOne.Health > _playerTwo.Health ? 1 : 2);
    }

    private async void EndRound(int winnerId)
    {
        if (!_roundActive || _transitioning)
            return;
        _roundActive = false;
        _transitioning = true;
        _playerOne.InputEnabled = false;
        _playerTwo.InputEnabled = false;
        if (winnerId == 1)
            _playerOneWins++;
        else if (winnerId == 2)
            _playerTwoWins++;
        _messageLabel.Text = winnerId == 0 ? "DRAW - ROUND REPLAY" : $"PLAYER {winnerId} WINS ROUND";
        GD.Print($"Round ended. Winner: {(winnerId == 0 ? "draw" : winnerId)}, score: {_playerOneWins}-{_playerTwoWins}");
        UpdateHud();
        await ToSignal(GetTree().CreateTimer(3.0), SceneTreeTimer.SignalName.Timeout);
        if (_playerOneWins >= WinsRequired || _playerTwoWins >= WinsRequired)
        {
            var matchWinner = _playerOneWins >= WinsRequired ? 1 : 2;
            _messageLabel.Text = $"PLAYER {matchWinner} WINS MATCH\nR 키로 재시작";
            _transitioning = false;
            return;
        }
        await StartRound();
    }

    private void ClearBullets()
    {
        foreach (var node in GetTree().GetNodesInGroup("bullets"))
            node.QueueFree();
    }

    private void UpdateHud()
    {
        _scoreLabel.Text = $"PLAYER 1  {_playerOneWins}  :  {_playerTwoWins}  PLAYER 2 (BOT)";
        _timerLabel.Text = Mathf.CeilToInt(_roundTime).ToString("00");
        _playerOneStatus.Text = FormatStatus(_playerOne);
        _playerTwoStatus.Text = FormatStatus(_playerTwo);
    }

    private static string FormatStatus(Player player)
    {
        var ammo = new string('●', player.Ammo) + new string('○', 4 - player.Ammo);
        var extra = player.IsReloading ? $"  재장전 {Mathf.RoundToInt(player.ReloadProgress * 100)}%" : string.Empty;
        if (player.ParryCooldownRatio > 0.0f)
            extra += $"  패링 {Mathf.RoundToInt((1.0f - player.ParryCooldownRatio) * 100)}%";
        return $"HP {player.Health:000}   {ammo}{extra}";
    }
}
