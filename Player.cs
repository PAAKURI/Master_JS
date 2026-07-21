using System;
using Godot;

public partial class Player : CharacterBody2D
{
    private static readonly PackedScene BulletScene = GD.Load<PackedScene>("res://Scene/bullet.tscn");

    private const float Speed = 400.0f;
    private const float JumpVelocity = -700.0f;
    private const float FastFallSpeed = 1100.0f;
    private const float WallJumpSpeed = 550.0f;
    private const float WallJumpLockDuration = 0.15f;
    private const float BulletSpeed = 1100.0f;
    private const float MuzzleDistance = 60.0f;
    private const float ShootInterval = 0.7f;
    private const int MaxHealth = 100;
    private const float HitInvulnerability = 0.15f;
    private const float ParryStartup = 0.05f;
    private const float ParryActive = 0.15f;
    private const float ParryRecovery = 0.25f;
    private const float ParryCooldown = 0.8f;

    private enum ParryState { Idle, Startup, Active, Recovery }

    private float _wallJumpLock;
    private bool _airJumpAvailable = true;
    private float _shootCooldown;
    private int _health = MaxHealth;
    private bool _invulnerable;
    private ParryState _parryState;
    private float _parryTime;
    private float _parryCooldown;

    private ProgressBar _healthBar = null!;
    private Area2D _parryArea = null!;
    private CollisionShape2D _parryCollision = null!;
    private Polygon2D _parryVisual = null!;
    private Sprite2D _playerSprite = null!;

    public override void _Ready()
    {
        _healthBar = GetNode<ProgressBar>("HealthBar");
        _parryArea = GetNode<Area2D>("ParryArea");
        _parryCollision = GetNode<CollisionShape2D>("ParryArea/CollisionShape2D");
        _parryVisual = GetNode<Polygon2D>("ParryArea/Visual");
        _playerSprite = GetNode<Sprite2D>("Player");
        _parryArea.BodyEntered += OnParryBodyEntered;
    }

    public override void _PhysicsProcess(double deltaValue)
    {
        var delta = (float)deltaValue;
        var direction = Input.GetAxis("move_left", "move_right");
        _wallJumpLock = Mathf.Max(_wallJumpLock - delta, 0.0f);
        _shootCooldown = Mathf.Max(_shootCooldown - delta, 0.0f);
        _parryCooldown = Mathf.Max(_parryCooldown - delta, 0.0f);
        UpdateParry(delta);

        if (Input.IsActionJustPressed("parry") && _parryCooldown == 0.0f)
        {
            _parryState = ParryState.Startup;
            _parryTime = ParryStartup;
            _parryCooldown = ParryCooldown;
        }

        if (Input.IsMouseButtonPressed(MouseButton.Left) && _shootCooldown == 0.0f && _parryState != ParryState.Active)
            Shoot();

        if (_wallJumpLock == 0.0f)
            Velocity = new Vector2(direction * Speed, Velocity.Y);

        if (!IsOnFloor())
        {
            Velocity += GetGravity() * delta;
            if (IsOnWall() && direction * GetWallNormal().X < 0.0f)
            {
                if (Input.IsActionJustPressed("jump"))
                {
                    Velocity = new Vector2(GetWallNormal().X * WallJumpSpeed, JumpVelocity);
                    _wallJumpLock = WallJumpLockDuration;
                }
                else
                {
                    Velocity = new Vector2(Velocity.X, 0.0f);
                }
            }
            else if (Input.IsActionJustPressed("jump") && _airJumpAvailable)
            {
                Velocity = new Vector2(Velocity.X, JumpVelocity);
                _airJumpAvailable = false;
            }
            else if (Input.IsActionPressed("move_down"))
            {
                Velocity = new Vector2(Velocity.X, Mathf.Max(Velocity.Y, FastFallSpeed));
            }
        }
        else
        {
            _airJumpAvailable = true;
            if (Input.IsActionJustPressed("jump"))
                Velocity = new Vector2(Velocity.X, JumpVelocity);
        }

        MoveAndSlide();
    }

    private void Shoot()
    {
        var mousePosition = GetGlobalMousePosition();
        if (mousePosition == GlobalPosition)
            return;

        var aim = Vector2.FromAngle(GlobalPosition.AngleToPoint(mousePosition));
        var bullet = BulletScene.Instantiate<Bullet>();
        GetTree().CurrentScene.AddChild(bullet);
        bullet.SetOwnerPlayer(this);
        bullet.GlobalPosition = GlobalPosition + aim * MuzzleDistance;
        var displacement = mousePosition - bullet.GlobalPosition;
        var travelTime = Mathf.Max(displacement.Length() / BulletSpeed, 0.01f);
        bullet.LinearVelocity = displacement / travelTime - GetGravity() * travelTime * 0.5f;
        _shootCooldown = ShootInterval;
    }

    public void TakeDamage(int amount)
    {
        if (_invulnerable || _health <= 0)
            return;

        _health = Math.Max(_health - amount, 0);
        _healthBar.Value = _health;
        GetTree().CallGroup("camera_shake", "shake", 10.0f, 0.18f);
        GetTree().CallGroup("damage_overlay", "show_damage");
        if (_health == 0)
        {
            SetPhysicsProcess(false);
            Velocity = Vector2.Zero;
            (GetTree().GetFirstNodeInGroup("game_over") as GameOverPanel)?.ShowGameOver();
            return;
        }

        _invulnerable = true;
        GetTree().CreateTimer(HitInvulnerability).Timeout += () => _invulnerable = false;
    }

    private void UpdateParry(float delta)
    {
        if (_parryState == ParryState.Idle)
            return;

        _parryTime -= delta;
        if (_parryTime > 0.0f)
            return;

        switch (_parryState)
        {
            case ParryState.Startup:
                _parryState = ParryState.Active;
                _parryTime = ParryActive;
                _parryArea.Monitoring = true;
                _parryCollision.SetDeferred(CollisionShape2D.PropertyName.Disabled, false);
                _parryVisual.Visible = true;
                break;
            case ParryState.Active:
                _parryState = ParryState.Recovery;
                _parryTime = ParryRecovery;
                _parryArea.Monitoring = false;
                _parryCollision.SetDeferred(CollisionShape2D.PropertyName.Disabled, true);
                _parryVisual.Visible = false;
                break;
            case ParryState.Recovery:
                _parryState = ParryState.Idle;
                break;
        }
    }

    private void OnParryBodyEntered(Node2D body)
    {
        if (body is Bullet bullet)
            TryParry(bullet, bullet.LinearVelocity);
    }

    public bool TryParry(Node2D body, Vector2 incomingVelocity)
    {
        if (_parryState != ParryState.Active || body is not Bullet bullet)
            return false;
        if (!bullet.Parry(this, incomingVelocity))
            return false;

        GetTree().CallGroup("camera_shake", "shake", 7.0f, 0.12f);
        _playerSprite.Modulate = new Color(0.5f, 1.0f, 1.0f);
        CreateTween().TweenProperty(_playerSprite, "modulate", Colors.White, 0.15);
        return true;
    }
}
