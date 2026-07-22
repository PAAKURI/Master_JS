using System;
using System.Collections.Generic;
using Godot;

public partial class Player : CharacterBody2D
{
	[Signal] public delegate void DiedEventHandler(int playerId);

	private static readonly PackedScene BulletScene = GD.Load<PackedScene>("res://Scene/bullet.tscn");
	private static readonly AudioStream ShootSound = GD.Load<AudioStream>("res://resources/shoot.mp3");
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
	private const float PlayerPushSpeed = 200.0f;
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
	private const double ReplicaInterpolationDelay = 0.1;
	private const double ReplicaExtrapolationLimit = 0.1;
	private const float PredictionDeadZone = 6.0f;
	private const float PredictionFloorVerticalDeadZone = 12.0f;
	private const float PredictionSnapDistance = 120.0f;

	private enum ParryState { Idle, Startup, Active, Recovery }

	[Export] public int PlayerId { get; set; } = 1;
	[Export] public bool IsBot { get; set; }
	[Export] public bool PreviewMode { get; set; }
	[Export] public Color PlayerColor { get; set; } = new(0.2f, 0.65f, 1.0f);
	[Export(PropertyHint.Range, "1.0,3.0,0.1")] public float SpriteGlowIntensity { get; set; } = 1.8f;

	public bool UsesRemoteInput { get; set; }
	public bool IsNetworkReplica { get; set; }
	public bool IsPredictedLocal { get; set; }
	public uint LastProcessedInputSequence { get; private set; }

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
	public uint ShootSequence { get; private set; }
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
	private readonly Queue<PlayerCommand> _commandQueue = new();
	private readonly List<PredictionSample> _predictionHistory = new();
	private readonly List<ReplicaSample> _replicaSamples = new();
	private uint _lastQueuedInputSequence;
	private float _inputLatencyCompensation;
	private Vector2 _predictionCorrection;
	private Vector2 _replicaPosition;
	private Vector2 _replicaVelocity;
	private bool _replicaOnFloor;
	private bool _replicaOnWall;
	private Color _spriteGlowColor;
	private bool _visualsEnabled = true;

	private readonly record struct PredictionSample(uint Sequence, Vector2 Position, Vector2 Velocity);
	private readonly record struct ReplicaSample(double ServerTime, double ReceivedTime, Vector2 Position, Vector2 Velocity);

	private CollisionShape2D _bodyCollision = null!;
	private Area2D _parryArea = null!;
	private CollisionShape2D _parryCollision = null!;
	private Polygon2D _parryVisual = null!;
	private Node2D _bodyVisual = null!;
	private Sprite2D _head = null!;
	private Sprite2D _mouth = null!;
	private Sprite2D _leftLeg = null!;
	private Sprite2D _rightLeg = null!;
	private EyeBall _leftEye = null!;
	private EyeBall _rightEye = null!;
	private Polygon2D _leftEyeVisual = null!;
	private Polygon2D _rightEyeVisual = null!;
	private Vector2 _leftLegRestPosition;
	private Vector2 _rightLegRestPosition;
	private float _leftLegAngularVelocity;
	private float _rightLegAngularVelocity;
	private ProgressBar _healthBar = null!;
	private ProgressBar _reloadBar = null!;
	private TextureProgressBar _parryCooldownIndicator = null!;
	private Polygon2D[] _ammoIndicators = null!;
	private AudioStreamPlayer _shootAudio = null!;

	public override void _Ready()
	{
		if (!PreviewMode)
		{
			AddToGroup("players");
			CollisionMask |= CollisionLayer;
		}
		_bodyCollision = GetNode<CollisionShape2D>("CollisionShape2D");
		_parryArea = GetNode<Area2D>("ParryArea");
		_parryCollision = GetNode<CollisionShape2D>("ParryArea/CollisionShape2D");
		_parryVisual = GetNode<Polygon2D>("ParryArea/Visual");
		_bodyVisual = GetNode<Node2D>("BodyVisual");
		_head = GetNode<Sprite2D>("BodyVisual/Head");
		_mouth = GetNode<Sprite2D>("BodyVisual/Tuck");
		_leftLeg = GetNode<Sprite2D>("BodyVisual/LeftLeg");
		_rightLeg = GetNode<Sprite2D>("BodyVisual/RightLeg");
		_leftEye = GetNode<EyeBall>("LeftEye");
		_rightEye = GetNode<EyeBall>("RightEye");
		_leftEyeVisual = GetNode<Polygon2D>("LeftEye/Visual");
		_rightEyeVisual = GetNode<Polygon2D>("RightEye/Visual");
		AnchorLegAtTop(_leftLeg);
		AnchorLegAtTop(_rightLeg);
		_leftLegRestPosition = _leftLeg.Position;
		_rightLegRestPosition = _rightLeg.Position;
		_legPhase = GD.RandRange(0.0, Mathf.Tau);
		SnapLegs();
		_healthBar = GetNode<ProgressBar>("HealthBar");
		_reloadBar = GetNode<ProgressBar>("ReloadBar");
		_parryCooldownIndicator = GetNode<TextureProgressBar>("ParryCooldown");
		_shootAudio = new AudioStreamPlayer { Stream = ShootSound };
		AddChild(_shootAudio);
		_ammoIndicators = new[]
		{
			GetNode<Polygon2D>("AmmoDisplay/Bullet1"),
			GetNode<Polygon2D>("AmmoDisplay/Bullet2"),
			GetNode<Polygon2D>("AmmoDisplay/Bullet3"),
			GetNode<Polygon2D>("AmmoDisplay/Bullet4")
		};
		_spriteGlowColor = new Color(PlayerColor.R * SpriteGlowIntensity, PlayerColor.G * SpriteGlowIntensity, PlayerColor.B * SpriteGlowIntensity, PlayerColor.A);
		_bodyVisual.Modulate = _spriteGlowColor;
		GetNode<Node2D>("AmmoDisplay").Modulate = PlayerColor;
		_parryVisual.Color = new Color(PlayerColor.R, PlayerColor.G, PlayerColor.B, 0.34f);
		_healthBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = PlayerColor });
		_reloadBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = PlayerColor });
		_parryCooldownIndicator.TintProgress = PlayerColor;
		_healthBar.Value = Health;
		UpdateAmmoDisplay();
		_parryArea.BodyEntered += OnParryBodyEntered;
		_botShootDelay = (float)GD.RandRange(0.5, 1.2);
		if (PreviewMode)
		{
			InputEnabled = false;
			CombatEnabled = false;
			CollisionLayer = 0;
			CollisionMask = 0;
			_bodyCollision.SetDeferred(CollisionShape2D.PropertyName.Disabled, true);
			_parryArea.Monitoring = false;
			_parryCollision.SetDeferred(CollisionShape2D.PropertyName.Disabled, true);
			_parryVisual.Visible = false;
			_healthBar.Visible = false;
			_reloadBar.Visible = false;
			GetNode<Node2D>("AmmoDisplay").Visible = false;
			_parryCooldownIndicator.Visible = false;
			SetProcess(false);
			SetPhysicsProcess(false);
		}
	}

	public void ApplyCustomization(CharacterLook look)
	{
		look = look.Sanitized();
		PlayerColor = look.BodyColor;
		_spriteGlowColor = new Color(
			look.BodyColor.R * SpriteGlowIntensity,
			look.BodyColor.G * SpriteGlowIntensity,
			look.BodyColor.B * SpriteGlowIntensity,
			look.BodyColor.A);
		_bodyVisual.Modulate = _spriteGlowColor;
		GetNode<Node2D>("AmmoDisplay").Modulate = look.BodyColor;
		_parryVisual.Color = new Color(look.BodyColor.R, look.BodyColor.G, look.BodyColor.B, 0.34f);
		_healthBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = look.BodyColor });
		_reloadBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = look.BodyColor });
		_parryCooldownIndicator.TintProgress = look.BodyColor;

		var eyePolygon = CharacterCustomization.BuildEyePolygon(look.EyeShape);
		_leftEyeVisual.Polygon = eyePolygon;
		_rightEyeVisual.Polygon = (Vector2[])eyePolygon.Clone();
		_leftEyeVisual.Color = look.EyeColor;
		_rightEyeVisual.Color = look.EyeColor;
		_leftEye.ConfigurePhysics(look.EyeSpring, look.EyeDamping, look.EyeChaos);
		_rightEye.ConfigurePhysics(look.EyeSpring, look.EyeDamping, look.EyeChaos);
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
			UpdateReplica();
			UpdateLegs(delta);
			return;
		}

		ApplyPredictionCorrection(delta);
		TickTimers(delta);
		UpdateParry(delta);
		ReadActions(delta, out var direction, out var jumpPressed, out var downPressed, out var upPressed, out var shootPressed, out var parryPressed);
		UpdateAim();

		if (!InputEnabled || !IsAlive)
		{
			Velocity += GetGravity() * delta;
			MoveAndSlide();
			KeepInsideArena();
			UpdateLegs(delta);
			RecordPrediction();
			return;
		}

		if (jumpPressed)
			_jumpBuffer = JumpBufferDuration;
		if (parryPressed)
			StartParry();
		if (shootPressed && !IsPredictedLocal)
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
		PushPlayers(delta);
		KeepInsideArena();
		UpdateLegs(delta);
		RecordPrediction();
	}

	public void SetTarget(Player target) => _target = target;

	public void EnqueueCommand(PlayerCommand command)
	{
		var sanitized = command.Sanitized();
		if (sanitized.Sequence != 0 && _lastQueuedInputSequence != 0 && !NetworkProtocol.IsNewer(sanitized.Sequence, _lastQueuedInputSequence))
			return;
		if (sanitized.Sequence != 0)
			_lastQueuedInputSequence = sanitized.Sequence;
		_commandQueue.Enqueue(sanitized);
		while (_commandQueue.Count > 32)
			_commandQueue.Dequeue();
	}

	public PlayerCommand CaptureLocalCommand()
	{
		var aim = GetGlobalMousePosition() - GlobalPosition;
		return new PlayerCommand(
			Input.GetAxis("move_left", "move_right"),
			Input.IsActionJustPressed("jump"),
			Input.IsActionPressed("move_down"),
			Input.IsActionPressed("move_up"),
			Input.IsActionPressed("shoot") || Input.IsActionJustPressed("shoot"),
			Input.IsActionJustPressed("parry"),
			aim.LengthSquared() > 1.0f ? aim.Normalized() : AimDirection);
	}

	public void ApplyNetworkState(uint serverTick, Vector2 position, Vector2 velocity, Vector2 aim, int health, int ammo, float reloadTime, float parryCooldown, bool alive, bool onFloor, bool onWall)
	{
		_replicaPosition = position;
		_replicaVelocity = velocity;
		ApplyAuthoritativeFields(aim, health, ammo, reloadTime, parryCooldown, alive);
		_replicaOnFloor = onFloor;
		_replicaOnWall = onWall;
		if (_replicaSamples.Count > 0 && _replicaSamples[^1].Position.DistanceTo(position) > 240.0f)
			_replicaSamples.Clear();
		var now = Time.GetTicksMsec() / 1000.0;
		_replicaSamples.Add(new ReplicaSample(serverTick / 60.0, now, position, velocity));
		if (_replicaSamples.Count > 32)
			_replicaSamples.RemoveAt(0);
		if (_replicaSamples.Count == 1 || GlobalPosition.DistanceTo(position) > 240.0f)
			GlobalPosition = position;
	}

	public void ReconcileNetworkState(uint acknowledgedSequence, Vector2 position, Vector2 velocity, Vector2 aim, int health, int ammo, float reloadTime, float parryCooldown, bool alive, bool onFloor, bool onWall)
	{
		ApplyAuthoritativeFields(aim, health, ammo, reloadTime, parryCooldown, alive);
		_replicaOnFloor = onFloor;
		_replicaOnWall = onWall;
		var predictedPosition = GlobalPosition;
		for (var index = 0; index < _predictionHistory.Count; index++)
		{
			if (_predictionHistory[index].Sequence != acknowledgedSequence)
				continue;
			predictedPosition = _predictionHistory[index].Position;
			break;
		}
		_predictionHistory.RemoveAll(sample => sample.Sequence == acknowledgedSequence || NetworkProtocol.IsNewer(acknowledgedSequence, sample.Sequence));
		var error = position - predictedPosition;
		if (onFloor && IsOnFloor() && Mathf.Abs(error.Y) < PredictionFloorVerticalDeadZone)
			error = new Vector2(error.X, 0.0f);
		if (error.Length() > PredictionSnapDistance)
		{
			GlobalPosition += error;
			_predictionCorrection = Vector2.Zero;
			Velocity = velocity;
		}
		else if (error.Length() > PredictionDeadZone)
		{
			_predictionCorrection = error;
			if (Velocity.DistanceTo(velocity) > 300.0f)
				Velocity = Velocity.Lerp(velocity, 0.5f);
		}
		else
		{
			_predictionCorrection = Vector2.Zero;
		}
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
		_commandQueue.Clear();
		_predictionHistory.Clear();
		_replicaSamples.Clear();
		_remoteCommand = PlayerCommand.Neutral;
		_lastQueuedInputSequence = 0;
		_inputLatencyCompensation = 0.0f;
		LastProcessedInputSequence = 0;
		_predictionCorrection = Vector2.Zero;
		foreach (var child in GetChildren())
			if (child is EyeBall eye)
				eye.CallDeferred(EyeBall.MethodName.SnapToAnchor);
		SetPhysicsProcess(true);
	}

	public void ResetPreview(Vector2 spawnPosition)
	{
		ResetForRound(spawnPosition);
		InputEnabled = false;
		CombatEnabled = false;
		SetPhysicsProcess(false);
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
		CreateTween().TweenProperty(_bodyVisual, "modulate", _spriteGlowColor, HitInvulnerability);
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
		CreateTween().TweenProperty(_bodyVisual, "modulate", _spriteGlowColor, 0.15);
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
			var latest = _remoteCommand;
			jump = false;
			parry = false;
			shoot = latest.Shoot;
			while (_commandQueue.Count > 0)
			{
				var command = _commandQueue.Dequeue();
				jump |= command.Jump;
				parry |= command.Parry;
				shoot |= command.Shoot;
				latest = command;
			}
			_remoteCommand = latest with { Jump = false, Parry = false };
			direction = latest.Move;
			down = latest.Down;
			up = latest.Up;
			_inputLatencyCompensation = Mathf.Clamp(latest.LatencyCompensation, 0.0f, 0.1f);
			if (latest.Sequence != 0)
				LastProcessedInputSequence = latest.Sequence;
			return;
		}

		if (!IsBot)
		{
			direction = Input.GetAxis("move_left", "move_right");
			jump = Input.IsActionJustPressed("jump");
			down = Input.IsActionPressed("move_down");
			up = Input.IsActionPressed("move_up");
			shoot = Input.IsActionPressed("shoot") || Input.IsActionJustPressed("shoot");
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

	public void SetVisualsEnabled(bool enabled)
	{
		_visualsEnabled = enabled;
		Visible = enabled;
		SetProcess(enabled);
		foreach (var child in GetChildren())
			if (child is EyeBall eye)
			{
				eye.Freeze = !enabled;
				eye.Sleeping = !enabled;
			}
	}

	private void ApplyAuthoritativeFields(Vector2 aim, int health, int ammo, float reloadTime, float parryCooldown, bool alive)
	{
		AimDirection = aim.IsFinite() && aim.LengthSquared() > 0.0001f ? aim.Normalized() : AimDirection;
		Health = Mathf.Clamp(health, 0, MaxHealth);
		Ammo = Mathf.Clamp(ammo, 0, MagazineSize);
		_reloadTime = float.IsFinite(reloadTime) ? Mathf.Max(reloadTime, 0.0f) : 0.0f;
		_parryCooldown = float.IsFinite(parryCooldown) ? Mathf.Max(parryCooldown, 0.0f) : 0.0f;
		IsAlive = alive;
		if (IsNetworkReplica)
		{
			var parryElapsed = ParryCooldownDuration - _parryCooldown;
			_parryVisual.Visible = alive && parryElapsed >= ParryStartup && parryElapsed < ParryStartup + ParryActive;
		}
		_healthBar.Value = Health;
	}

	private void ApplyPredictionCorrection(float delta)
	{
		if (!IsPredictedLocal || _predictionCorrection.LengthSquared() < 0.01f)
			return;
		var step = _predictionCorrection * Mathf.Min(delta * 10.0f, 0.5f);
		GlobalPosition += step;
		_predictionCorrection -= step;
	}

	private void RecordPrediction()
	{
		if (!IsPredictedLocal || LastProcessedInputSequence == 0)
			return;
		if (_predictionHistory.Count > 0 && _predictionHistory[^1].Sequence == LastProcessedInputSequence)
			_predictionHistory[^1] = new PredictionSample(LastProcessedInputSequence, GlobalPosition, Velocity);
		else
			_predictionHistory.Add(new PredictionSample(LastProcessedInputSequence, GlobalPosition, Velocity));
		if (_predictionHistory.Count > 180)
			_predictionHistory.RemoveAt(0);
	}

	private void UpdateReplica()
	{
		if (_replicaSamples.Count == 0)
			return;
		var latest = _replicaSamples[^1];
		var now = Time.GetTicksMsec() / 1000.0;
		var targetTime = latest.ServerTime + (now - latest.ReceivedTime) - ReplicaInterpolationDelay;
		while (_replicaSamples.Count > 2 && _replicaSamples[1].ServerTime <= targetTime)
			_replicaSamples.RemoveAt(0);

		var targetPosition = latest.Position;
		var targetVelocity = latest.Velocity;
		if (_replicaSamples.Count >= 2 && targetTime <= _replicaSamples[1].ServerTime)
		{
			var from = _replicaSamples[0];
			var to = _replicaSamples[1];
			var duration = Math.Max(to.ServerTime - from.ServerTime, 0.0001);
			var weight = Mathf.Clamp((float)((targetTime - from.ServerTime) / duration), 0.0f, 1.0f);
			targetPosition = from.Position.Lerp(to.Position, weight);
			targetVelocity = from.Velocity.Lerp(to.Velocity, weight);
		}
		else
		{
			var extrapolation = Math.Clamp(targetTime - latest.ServerTime, 0.0, ReplicaExtrapolationLimit);
			targetPosition = latest.Position + latest.Velocity * (float)extrapolation;
		}

		GlobalPosition = targetPosition;
		Velocity = targetVelocity;
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
		if (!_visualsEnabled)
			return;
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

	private void PushPlayers(float delta)
	{
		if (!CombatEnabled)
			return;
		for (var index = 0; index < GetSlideCollisionCount(); index++)
		{
			var collision = GetSlideCollision(index);
			if (collision.GetCollider() is Player other && other.CombatEnabled)
				other.MoveAndCollide(-collision.GetNormal() * PlayerPushSpeed * delta);
		}
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
		var bulletParent = PreviewMode ? GetParent() : GetTree().CurrentScene;
		bulletParent.AddChild(bullet);
		bullet.GlobalPosition = _mouth.GlobalPosition;
		bullet.SetOwnerPlayer(this);
		bullet.LinearVelocity = AimDirection * BulletSpeed;
		bullet.FastForward(_inputLatencyCompensation);
		ShootSequence++;
		PlayShootAnimation();
		Velocity -= AimDirection * RecoilStrength;
		Ammo--;
		_shootCooldown = ShootInterval;
		GetTree().CallGroup("camera_shake", "shake", 2.0f, 0.06f);
		if (Ammo <= 0)
			BeginReload();
	}

	public void PlayShootAnimation()
	{
		_shootAudio.Play();
		var headPosition = _head.Position;
		var headTween = CreateTween();
		headTween.TweenProperty(_head, "position", headPosition + Vector2.Up * 8.0f, 0.05);
		headTween.TweenProperty(_head, "position", headPosition, 0.08);
	}

	public void ApplyNetworkShootSequence(uint sequence)
	{
		if (sequence == ShootSequence)
			return;
		ShootSequence = sequence;
		if (sequence != 0)
			PlayShootAnimation();
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
		var authoritativeActive = active && CombatEnabled;
		_parryArea.Monitoring = authoritativeActive;
		_parryCollision.SetDeferred(CollisionShape2D.PropertyName.Disabled, !authoritativeActive);
		_parryVisual.Visible = active;
	}

	private void OnParryBodyEntered(Node2D body)
	{
		if (body is Bullet bullet)
			TryParry(bullet, bullet.LinearVelocity);
	}
}
