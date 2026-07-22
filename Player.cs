using System;
using Godot;

public partial class Player : CharacterBody2D
{
	[Signal] public delegate void DiedEventHandler(int playerId);

	private static readonly PackedScene BulletScene = GD.Load<PackedScene>("res://Scene/bullet.tscn");
	private static readonly Vector2 ArenaSize = new(1920.0f, 1080.0f);

	private const float Speed = 400.0f;
	private const float CrouchSpeed = 120.0f;
	private const float JumpVelocity = -400.0f;
	private const float WallClimbSpeed = 135.0f;
	private const float WallSlideSpeed = 170.0f;
	private const float WallJumpSpeed = 550.0f;
	private const float WallJumpLockDuration = 0.15f;
	private const float CoyoteDuration = 0.1f;
	private const float JumpBufferDuration = 0.1f;
	private const float BulletSpeed = 1100.0f;
	private const float ShootInterval = 0.7f;
	private const float ReloadDuration = 3.0f;
	private const float RecoilStrength = 145.0f;
	private const float ArenaMargin = 48.0f;
	private const float HitInvulnerability = 0.15f;
	private const float ParryStartup = 0.05f;
	private const float ParryActive = 0.15f;
	private const float ParryRecovery = 0.25f;
	private const float ParryCooldownDuration = 0.8f;
	private const float LegSpringStrength = 58.0f;
	private const float LegDamping = 7.5f;
	private const float LegChaos = 12.0f;
	private const float LegWalkSpeed = 12.0f;
	private static readonly float LegWalkRotation = Mathf.DegToRad(55.0f);
	private static readonly float LegMaxRotation = Mathf.DegToRad(130.0f);
	private const int MaxHealth = 100;
	private const int MagazineSize = 4;

	private enum ParryState { Idle, Startup, Active, Recovery }

	[Export] public int PlayerId { get; set; } = 1;
	[Export] public bool IsBot { get; set; }
	[Export] public Color PlayerColor { get; set; } = new(0.2f, 0.65f, 1.0f);

	public bool UsesRemoteInput { get; set; }
	public bool IsNetworkReplica { get; set; }

	public int Health { get; private set; } = MaxHealth;
	private int _ammo = MagazineSize;
	public int Ammo
	{
		get => _ammo;
		private set
		{
			_ammo = value;
			UpdateAmmoDisplay();
		}
	}
	public bool IsReloading => _reloadTime > 0.0f;
	public float ReloadProgress => IsReloading ? 1.0f - _reloadTime / ReloadDuration : 0.0f;
	public float ParryCooldownRatio => _parryCooldown / ParryCooldownDuration;
	public float ReloadSeconds => _reloadTime;
	public float ParryCooldownSeconds => _parryCooldown;
	public bool IsAlive { get; private set; } = true;
	public bool OnFloor => IsOnFloor();
	public bool OnWall => IsOnWall();
	public bool InputEnabled { get; set; }
	public bool CombatEnabled { get; set; }
	public Vector2 AimDirection { get; private set; } = Vector2.Right;

	private float _wallJumpLock;
	private float _coyoteTime;
	private float _jumpBuffer;
	private float _shootCooldown;
	private float _reloadTime;
	private float _invulnerabilityTime;
	private float _parryCooldown;
	private float _parryTime;
	private float _botShootDelay;
	private float _botParryDelay;
	private float _botJumpDelay;
	private double _legPhase;
	private ParryState _parryState;
	private Player? _target;
	private PlayerCommand _remoteCommand = PlayerCommand.Neutral;
	private Vector2 _replicaPosition;
	private Vector2 _replicaVelocity;
	private bool _replicaOnFloor;
	private bool _replicaOnWall;

	private CollisionShape2D _bodyCollision = null!;
	private Area2D _parryArea = null!;
	private CollisionShape2D _parryCollision = null!;
	private Polygon2D _parryVisual = null!;
	private Node2D _bodyVisual = null!;
	private Sprite2D _head = null!;
	private Sprite2D _mouth = null!;
	private Sprite2D _leftLeg = null!;
	private Sprite2D _rightLeg = null!;
	private Vector2 _leftLegRestPosition;
	private Vector2 _rightLegRestPosition;
	private float _leftLegAngularVelocity;
	private float _rightLegAngularVelocity;
	private ProgressBar _healthBar = null!;
	private ProgressBar _reloadBar = null!;
	private TextureProgressBar _parryCooldownIndicator = null!;
	private Polygon2D[] _ammoIndicators = null!;

	public override void _Ready()
	{
		AddToGroup("players");
		_bodyCollision = GetNode<CollisionShape2D>("CollisionShape2D");
		_parryArea = GetNode<Area2D>("ParryArea");
		_parryCollision = GetNode<CollisionShape2D>("ParryArea/CollisionShape2D");
		_parryVisual = GetNode<Polygon2D>("ParryArea/Visual");
		_bodyVisual = GetNode<Node2D>("BodyVisual");
		_head = GetNode<Sprite2D>("BodyVisual/Head");
		_mouth = GetNode<Sprite2D>("BodyVisual/Tuck");
		_leftLeg = GetNode<Sprite2D>("BodyVisual/LeftLeg");
		_rightLeg = GetNode<Sprite2D>("BodyVisual/RightLeg");
		AnchorLegAtTop(_leftLeg);
		AnchorLegAtTop(_rightLeg);
		_leftLegRestPosition = _leftLeg.Position;
		_rightLegRestPosition = _rightLeg.Position;
		_legPhase = GD.RandRange(0.0, Mathf.Tau);
		SnapLegs();
		_healthBar = GetNode<ProgressBar>("HealthBar");
		_reloadBar = GetNode<ProgressBar>("ReloadBar");
		_parryCooldownIndicator = GetNode<TextureProgressBar>("ParryCooldown");
		_ammoIndicators = new[]
		{
			GetNode<Polygon2D>("AmmoDisplay/Bullet1"),
			GetNode<Polygon2D>("AmmoDisplay/Bullet2"),
			GetNode<Polygon2D>("AmmoDisplay/Bullet3"),
			GetNode<Polygon2D>("AmmoDisplay/Bullet4")
		};
		_bodyVisual.Modulate = PlayerColor;
		GetNode<Node2D>("AmmoDisplay").Modulate = PlayerColor;
		_parryVisual.Color = new Color(PlayerColor.R, PlayerColor.G, PlayerColor.B, 0.34f);
		_healthBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = PlayerColor });
		_reloadBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = PlayerColor });
		_parryCooldownIndicator.TintProgress = PlayerColor;
		_healthBar.Value = Health;
		UpdateAmmoDisplay();
		_parryArea.BodyEntered += OnParryBodyEntered;
		_botShootDelay = (float)GD.RandRange(0.5, 1.2);
	}

	public override void _Process(double _)
	{
		_reloadBar.Visible = IsReloading;
		_reloadBar.Value = ReloadProgress;
		_parryCooldownIndicator.Value = 1.0f - ParryCooldownRatio;
	}

	private void UpdateAmmoDisplay()
	{
		for (var index = 0; index < _ammoIndicators.Length; index++)
			_ammoIndicators[index].Visible = index < Ammo;
	}

	public override void _PhysicsProcess(double deltaValue)
	{
		var delta = (float)deltaValue;
		if (IsNetworkReplica)
		{
			GlobalPosition = GlobalPosition.Lerp(_replicaPosition, Mathf.Min(delta * 18.0f, 1.0f));
			Velocity = _replicaVelocity;
			UpdateLegs(delta);
			return;
		}

		TickTimers(delta);
		UpdateParry(delta);
		UpdateAim();

		if (!InputEnabled || !IsAlive)
		{
			Velocity += GetGravity() * delta;
			MoveAndSlide();
			KeepInsideArena();
			UpdateLegs(delta);
			return;
		}

		float direction;
		bool jumpPressed;
		bool downPressed;
		bool upPressed;
		bool shootPressed;
		bool parryPressed;
		ReadActions(delta, out direction, out jumpPressed, out downPressed, out upPressed, out shootPressed, out parryPressed);

		if (jumpPressed)
			_jumpBuffer = JumpBufferDuration;
		if (parryPressed)
			StartParry();
		if (shootPressed)
			TryShoot();

		var crouching = IsOnFloor() && downPressed;
		_bodyCollision.Scale = new Vector2(1.0f, crouching ? 0.62f : 1.0f);
		_bodyCollision.Position = new Vector2(0.0f, crouching ? 16.7f : 0.0f);
		_bodyVisual.Scale = new Vector2(1.0f, crouching ? 0.62f : 1.0f);
		_bodyVisual.Position = new Vector2(0.0f, crouching ? 16.7f : 0.0f);

		if (IsOnFloor())
			_coyoteTime = CoyoteDuration;

		var attachedToWall = !IsOnFloor() && IsOnWall() && direction * GetWallNormal().X < 0.0f;
		if (_wallJumpLock <= 0.0f)
			Velocity = new Vector2(direction * (crouching ? CrouchSpeed : Speed), Velocity.Y);

		if (attachedToWall)
		{
			if (_jumpBuffer > 0.0f)
			{
				Velocity = new Vector2(GetWallNormal().X * WallJumpSpeed, JumpVelocity);
				_wallJumpLock = WallJumpLockDuration;
				_jumpBuffer = 0.0f;
			}
			else
			{
				var wallVelocity = upPressed ? -WallClimbSpeed : downPressed ? WallClimbSpeed : WallSlideSpeed;
				Velocity = new Vector2(Velocity.X, wallVelocity);
			}
		}
		else
		{
			if (!IsOnFloor())
				Velocity += GetGravity() * delta;
			if (_jumpBuffer > 0.0f && _coyoteTime > 0.0f)
			{
				Velocity = new Vector2(Velocity.X, JumpVelocity);
				_jumpBuffer = 0.0f;
				_coyoteTime = 0.0f;
			}
		}

		MoveAndSlide();
		KeepInsideArena();
		UpdateLegs(delta);
	}

	public void SetTarget(Player target) => _target = target;

	public void SetRemoteCommand(PlayerCommand command) => _remoteCommand = command.Sanitized();

	public PlayerCommand CaptureLocalCommand()
	{
		var aim = GetGlobalMousePosition() - GlobalPosition;
		return new PlayerCommand(
			Input.GetAxis("move_left", "move_right"),
			Input.IsActionJustPressed("jump"),
			Input.IsActionPressed("move_down"),
			Input.IsActionPressed("move_up"),
			Input.IsMouseButtonPressed(MouseButton.Left),
			Input.IsActionJustPressed("parry"),
			aim.LengthSquared() > 1.0f ? aim.Normalized() : AimDirection);
	}

	public void ApplyNetworkState(Vector2 position, Vector2 velocity, Vector2 aim, int health, int ammo, float reloadTime, float parryCooldown, bool alive, bool onFloor, bool onWall)
	{
		_replicaPosition = position;
		_replicaVelocity = velocity;
		AimDirection = aim.LengthSquared() > 0.0001f ? aim.Normalized() : AimDirection;
		Health = Mathf.Clamp(health, 0, MaxHealth);
		Ammo = Mathf.Clamp(ammo, 0, MagazineSize);
		_reloadTime = Mathf.Max(reloadTime, 0.0f);
		_parryCooldown = Mathf.Max(parryCooldown, 0.0f);
		IsAlive = alive;
		_replicaOnFloor = onFloor;
		_replicaOnWall = onWall;
		_healthBar.Value = Health;
		if (GlobalPosition.DistanceTo(position) > 180.0f)
			GlobalPosition = position;
	}

	public void ResetForRound(Vector2 spawnPosition)
	{
		GlobalPosition = spawnPosition;
		Velocity = Vector2.Zero;
		Health = MaxHealth;
		Ammo = MagazineSize;
		IsAlive = true;
		InputEnabled = false;
		CombatEnabled = false;
		_shootCooldown = 0.0f;
		_reloadTime = 0.0f;
		_invulnerabilityTime = 0.0f;
		_parryCooldown = 0.0f;
		_parryState = ParryState.Idle;
		SetParryActive(false);
		_bodyCollision.Scale = Vector2.One;
		_bodyCollision.Position = Vector2.Zero;
		_bodyVisual.Scale = Vector2.One;
		_bodyVisual.Position = Vector2.Zero;
		SnapLegs();
		_healthBar.Value = Health;
		_replicaPosition = spawnPosition;
		_replicaVelocity = Vector2.Zero;
		foreach (var child in GetChildren())
			if (child is EyeBall eye)
				eye.CallDeferred(EyeBall.MethodName.SnapToAnchor);
		SetPhysicsProcess(true);
	}

	public void Kill()
	{
		if (!IsAlive)
			return;
		IsAlive = false;
		InputEnabled = false;
		EmitSignal(SignalName.Died, PlayerId);
	}

	public bool TakeHit(int amount, Vector2 bulletVelocity)
	{
		if (!CombatEnabled || !IsAlive || _invulnerabilityTime > 0.0f)
			return false;

		Health = Math.Max(Health - amount, 0);
		_healthBar.Value = Health;
		_invulnerabilityTime = HitInvulnerability;
		var knockbackDirection = bulletVelocity.LengthSquared() > 0.01f ? bulletVelocity.Normalized() : Vector2.Up;
		Velocity += knockbackDirection * 430.0f + Vector2.Up * 120.0f;
		GetTree().CallGroup("camera_shake", "shake", 10.0f, 0.18f);
		GetTree().CallGroup("damage_overlay", "show_damage", PlayerId);
		_bodyVisual.Modulate = Colors.White;
		CreateTween().TweenProperty(_bodyVisual, "modulate", PlayerColor, HitInvulnerability);
		if (Health <= 0)
			Kill();
		return true;
	}

	public bool TryParry(Bullet bullet, Vector2 incomingVelocity)
	{
		if (!IsAlive || _parryState != ParryState.Active)
			return false;
		if (!bullet.Parry(this, incomingVelocity))
			return false;

		GetTree().CallGroup("camera_shake", "shake", 7.0f, 0.12f);
		_bodyVisual.Modulate = new Color(0.6f, 1.0f, 1.0f);
		CreateTween().TweenProperty(_bodyVisual, "modulate", PlayerColor, 0.15);
		return true;
	}

	private void TickTimers(float delta)
	{
		_wallJumpLock = Mathf.Max(_wallJumpLock - delta, 0.0f);
		_coyoteTime = Mathf.Max(_coyoteTime - delta, 0.0f);
		_jumpBuffer = Mathf.Max(_jumpBuffer - delta, 0.0f);
		_shootCooldown = Mathf.Max(_shootCooldown - delta, 0.0f);
		_invulnerabilityTime = Mathf.Max(_invulnerabilityTime - delta, 0.0f);
		_parryCooldown = Mathf.Max(_parryCooldown - delta, 0.0f);
		if (_reloadTime <= 0.0f)
			return;
		_reloadTime = Mathf.Max(_reloadTime - delta, 0.0f);
		if (_reloadTime <= 0.0f)
			Ammo = MagazineSize;
	}

	private void ReadActions(float delta, out float direction, out bool jump, out bool down, out bool up, out bool shoot, out bool parry)
	{
		if (UsesRemoteInput)
		{
			var command = _remoteCommand;
			direction = command.Move;
			jump = command.Jump;
			down = command.Down;
			up = command.Up;
			shoot = command.Shoot;
			parry = command.Parry;
			_remoteCommand = command with { Jump = false, Parry = false };
			return;
		}

		if (!IsBot)
		{
			direction = Input.GetAxis("move_left", "move_right");
			jump = Input.IsActionJustPressed("jump");
			down = Input.IsActionPressed("move_down");
			up = Input.IsActionPressed("move_up");
			shoot = Input.IsMouseButtonPressed(MouseButton.Left);
			parry = Input.IsActionJustPressed("parry");
			return;
		}

		var toTarget = GodotObject.IsInstanceValid(_target) ? _target!.GlobalPosition - GlobalPosition : Vector2.Zero;
		direction = Mathf.Abs(toTarget.X) > 330.0f ? Mathf.Sign(toTarget.X) : Mathf.Abs(toTarget.X) < 150.0f ? -Mathf.Sign(toTarget.X) : 0.0f;
		_botJumpDelay -= delta;
		jump = (IsOnWall() || (IsOnFloor() && Mathf.Abs(toTarget.Y) > 130.0f)) && _botJumpDelay <= 0.0f;
		if (jump)
			_botJumpDelay = (float)GD.RandRange(0.8, 1.5);
		down = !IsOnFloor() && toTarget.Y > 160.0f;
		up = IsOnWall() && toTarget.Y < -80.0f;
		_botShootDelay -= delta;
		shoot = _botShootDelay <= 0.0f;
		if (shoot)
			_botShootDelay = (float)GD.RandRange(0.75, 1.3);
		_botParryDelay -= delta;
		parry = false;
		if (_botParryDelay <= 0.0f && HasIncomingBullet())
		{
			parry = true;
			_botParryDelay = 0.35f;
		}
	}

	private bool HasIncomingBullet()
	{
		foreach (var node in GetTree().GetNodesInGroup("bullets"))
		{
			if (node is not Bullet bullet || bullet.OwnerPlayer == this)
				continue;
			var offset = bullet.GlobalPosition - GlobalPosition;
			if (offset.Length() < 175.0f && bullet.LinearVelocity.Dot(offset) < 0.0f)
				return true;
		}
		return false;
	}

	private void UpdateAim()
	{
		if (UsesRemoteInput)
		{
			AimDirection = _remoteCommand.Aim;
			return;
		}

		Vector2 targetPosition;
		if (IsBot && GodotObject.IsInstanceValid(_target))
		{
			targetPosition = _target!.GlobalPosition;
			var distance = targetPosition.DistanceTo(GlobalPosition);
			targetPosition.Y -= Mathf.Clamp(distance * 0.18f, 25.0f, 210.0f);
		}
		else
		{
			targetPosition = GetGlobalMousePosition();
		}

		var aim = targetPosition - GlobalPosition;
		if (aim.LengthSquared() > 1.0f)
			AimDirection = aim.Normalized();
	}

	private void UpdateLegs(float delta)
	{
		var onFloor = IsNetworkReplica ? _replicaOnFloor : IsOnFloor();
		var onWall = IsNetworkReplica ? _replicaOnWall : IsOnWall();
		if (onFloor || onWall)
		{
			var speedRatio = onFloor
				? Mathf.Clamp(Mathf.Abs(Velocity.X) / Speed, 0.0f, 1.0f)
				: Mathf.Clamp(Mathf.Abs(Velocity.Y) / WallSlideSpeed, 0.0f, 1.0f);
			_legPhase += delta * LegWalkSpeed * speedRatio;
			var stride = Mathf.Sin((float)_legPhase) * LegWalkRotation * speedRatio;
			var blend = Mathf.Min(delta * 20.0f, 1.0f);
			_leftLeg.Rotation = Mathf.LerpAngle(_leftLeg.Rotation, -stride, blend);
			_rightLeg.Rotation = Mathf.LerpAngle(_rightLeg.Rotation, stride, blend);
			_leftLegAngularVelocity = 0.0f;
			_rightLegAngularVelocity = 0.0f;
			return;
		}

		_legPhase += delta * 17.0;
		UpdateAirLeg(_leftLeg, ref _leftLegAngularVelocity, (float)_legPhase, delta);
		UpdateAirLeg(_rightLeg, ref _rightLegAngularVelocity, (float)(_legPhase + Mathf.Pi), delta);
	}

	private void UpdateAirLeg(Sprite2D leg, ref float angularVelocity, float phase, float delta)
	{
		var targetRotation = -Mathf.Clamp(Velocity.X / Speed, -1.0f, 1.0f) * LegMaxRotation;
		var torque = (targetRotation - leg.Rotation) * LegSpringStrength
			- angularVelocity * LegDamping
			+ Mathf.Sin(phase) * LegChaos;
		angularVelocity -= torque * delta;
		leg.Rotation = Mathf.Clamp(leg.Rotation + angularVelocity * delta, -LegMaxRotation, LegMaxRotation);
	}

	private static void AnchorLegAtTop(Sprite2D leg)
	{
		var halfTextureHeight = leg.Texture.GetHeight() * 0.5f;
		leg.Position += Vector2.Up * halfTextureHeight * leg.Scale.Y;
		leg.Offset = new Vector2(0.0f, halfTextureHeight);
	}

	private void SnapLegs()
	{
		_leftLeg.Position = _leftLegRestPosition;
		_rightLeg.Position = _rightLegRestPosition;
		_leftLeg.Rotation = 0.0f;
		_rightLeg.Rotation = 0.0f;
		_leftLegAngularVelocity = 0.0f;
		_rightLegAngularVelocity = 0.0f;
	}

	private void KeepInsideArena()
	{
		var clampedPosition = new Vector2(
			Mathf.Clamp(GlobalPosition.X, ArenaMargin, ArenaSize.X - ArenaMargin),
			Mathf.Clamp(GlobalPosition.Y, ArenaMargin, ArenaSize.Y - ArenaMargin)
		);
		if (GlobalPosition == clampedPosition)
			return;

		var inward = (clampedPosition - GlobalPosition).Normalized();
		GlobalPosition = clampedPosition;
		if (_parryState == ParryState.Active)
		{
			Velocity = inward * WallJumpSpeed;
			_wallJumpLock = WallJumpLockDuration;
			return;
		}
		if (Velocity.Dot(inward) < 0.0f)
			Velocity = Velocity.Bounce(inward);
		TakeHit(50, inward);
	}

	private void TryShoot()
	{
		if (_shootCooldown > 0.0f || IsReloading || _parryState == ParryState.Startup || _parryState == ParryState.Active)
			return;
		if (Ammo <= 0)
		{
			BeginReload();
			return;
		}

		var bullet = BulletScene.Instantiate<Bullet>();
		GetTree().CurrentScene.AddChild(bullet);
		bullet.GlobalPosition = _mouth.GlobalPosition;
		bullet.SetOwnerPlayer(this);
		bullet.LinearVelocity = AimDirection * BulletSpeed;
		var headPosition = _head.Position;
		var headTween = CreateTween();
		headTween.TweenProperty(_head, "position", headPosition + Vector2.Up * 8.0f, 0.05);
		headTween.TweenProperty(_head, "position", headPosition, 0.08);
		Velocity -= AimDirection * RecoilStrength;
		Ammo--;
		_shootCooldown = ShootInterval;
		GetTree().CallGroup("camera_shake", "shake", 2.0f, 0.06f);
		if (Ammo <= 0)
			BeginReload();
	}

	private void BeginReload()
	{
		if (!IsReloading)
			_reloadTime = ReloadDuration;
	}

	private void StartParry()
	{
		if (_parryCooldown > 0.0f || _parryState != ParryState.Idle)
			return;
		_parryState = ParryState.Startup;
		_parryTime = ParryStartup;
		_parryCooldown = ParryCooldownDuration;
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
				SetParryActive(true);
				break;
			case ParryState.Active:
				_parryState = ParryState.Recovery;
				_parryTime = ParryRecovery;
				SetParryActive(false);
				break;
			case ParryState.Recovery:
				_parryState = ParryState.Idle;
				break;
		}
	}

	private void SetParryActive(bool active)
	{
		_parryArea.Monitoring = active;
		_parryCollision.SetDeferred(CollisionShape2D.PropertyName.Disabled, !active);
		_parryVisual.Visible = active;
	}

	private void OnParryBodyEntered(Node2D body)
	{
		if (body is Bullet bullet)
			TryParry(bullet, bullet.LinearVelocity);
	}
}
