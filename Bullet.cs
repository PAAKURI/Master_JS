using System;
using Godot;

public partial class Bullet : RigidBody2D
{
    private static int _nextNetworkId = 1;
    private const int MaxTrailPoints = 30;
    private const float LifetimeSeconds = 5.0f;
    private const int Damage = 25;
    private const float OwnerCollisionGrace = 0.15f;

    public Player? OwnerPlayer { get; private set; }
    public int NetworkId { get; private set; }
    public int OwnerPlayerId => GodotObject.IsInstanceValid(OwnerPlayer) ? OwnerPlayer!.PlayerId : 0;
    public bool IsReplica { get; private set; }

    private Line2D _trail = null!;
    private float _age;
    private bool _collisionQueued;
    private int _ownerVersion;
    private Vector2 _replicaTargetPosition;
    private double _replicaUpdatedAt;
    private bool _headless;

    public override void _Ready()
    {
        AddToGroup("bullets");
        _trail = GetNode<Line2D>("Trail");
        _headless = DisplayServer.GetName() == "headless";
        _trail.Visible = !_headless;
        if (NetworkId == 0)
            NetworkId = _nextNetworkId++;
    }

    public void ConfigureReplica(int networkId, Vector2 position, Vector2 velocity, Player? owner)
    {
        NetworkId = networkId;
        IsReplica = true;
        Freeze = true;
        CollisionLayer = 0;
        CollisionMask = 0;
        OwnerPlayer = owner;
        GlobalPosition = position;
        _replicaTargetPosition = position;
        _replicaUpdatedAt = Time.GetTicksMsec() / 1000.0;
        LinearVelocity = velocity;
    }

    public void ApplyReplicaState(Vector2 position, Vector2 velocity, Player? owner)
    {
        OwnerPlayer = owner;
        _replicaTargetPosition = position;
        _replicaUpdatedAt = Time.GetTicksMsec() / 1000.0;
        LinearVelocity = velocity;
    }

    public void SetOwnerPlayer(Player player)
    {
        _ownerVersion++;
        if (GodotObject.IsInstanceValid(OwnerPlayer))
            RemoveCollisionExceptionWith(OwnerPlayer!);
        OwnerPlayer = player;
        AddCollisionExceptionWith(player);
        var ignoredPlayer = player;
        var version = _ownerVersion;
        GetTree().CreateTimer(OwnerCollisionGrace).Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(this) && version == _ownerVersion && GodotObject.IsInstanceValid(ignoredPlayer))
                RemoveCollisionExceptionWith(ignoredPlayer);
        };
    }

    public void FastForward(float seconds)
    {
        var duration = Mathf.Clamp(seconds, 0.0f, 0.1f);
        if (IsReplica || duration <= 0.0f || LinearVelocity.LengthSquared() < 0.01f)
            return;
        var destination = GlobalPosition + LinearVelocity * duration;
        var exclude = new Godot.Collections.Array<Rid> { GetRid() };
        if (GodotObject.IsInstanceValid(OwnerPlayer))
            exclude.Add(OwnerPlayer!.GetRid());
        var query = PhysicsRayQueryParameters2D.Create(GlobalPosition, destination, CollisionMask, exclude);
        query.CollideWithAreas = false;
        var hit = GetWorld2D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0)
        {
            GlobalPosition = destination;
            return;
        }

        GlobalPosition = hit["position"].AsVector2();
        var collider = hit["collider"].AsGodotObject();
        if (GodotObject.IsInstanceValid(collider) && collider is Player target)
        {
            if (target.TryParry(this, LinearVelocity))
                return;
            target.TakeHit(Damage, LinearVelocity);
            QueueFree();
            return;
        }
        SpawnImpact(hit["normal"].AsVector2());
    }

    public bool Parry(Player player, Vector2 incomingVelocity)
    {
        if (_collisionQueued)
            return false;
        SetOwnerPlayer(player);
        Sleeping = false;
        LinearVelocity = -incomingVelocity * 1.15f;
        return true;
    }

    public override void _PhysicsProcess(double delta)
    {
        _age += (float)delta;
        if (_age >= LifetimeSeconds)
        {
            QueueFree();
            return;
        }
        if (IsReplica)
        {
            var sinceUpdate = Math.Clamp(Time.GetTicksMsec() / 1000.0 - _replicaUpdatedAt, 0.0, 0.1);
            var target = _replicaTargetPosition + LinearVelocity * (float)sinceUpdate;
            GlobalPosition = GlobalPosition.DistanceTo(target) > 180.0f
                ? target
                : GlobalPosition.Lerp(target, Mathf.Min((float)delta * 22.0f, 1.0f));
        }
        if (_headless)
            return;
        _trail.AddPoint(GlobalPosition);
        if (_trail.GetPointCount() > MaxTrailPoints)
            _trail.RemovePoint(0);
    }

    public override void _IntegrateForces(PhysicsDirectBodyState2D state)
    {
        if (IsReplica || _collisionQueued || state.GetContactCount() == 0)
            return;
        _collisionQueued = true;
        var velocity = state.LinearVelocity;
        var normal = state.GetContactLocalNormal(0).Normalized();
        var collider = state.GetContactColliderObject(0);
        state.LinearVelocity = Vector2.Zero;
        Callable.From(() => ResolveCollision(collider, normal, velocity)).CallDeferred();
    }

    private void ResolveCollision(GodotObject collider, Vector2 normal, Vector2 incomingVelocity)
    {
        if (GodotObject.IsInstanceValid(collider) && collider is Player player && player.TryParry(this, incomingVelocity))
        {
            _collisionQueued = false;
            return;
        }
        if (GodotObject.IsInstanceValid(collider) && collider is Player target)
        {
            target.TakeHit(Damage, incomingVelocity);
            QueueFree();
            return;
        }
        SpawnImpact(normal);
    }

    private void SpawnImpact(Vector2 normal)
    {
        if (_headless)
        {
            QueueFree();
            return;
        }
        GetTree().CallGroup("camera_shake", "shake", 4.0f, 0.1f);
        var impact = new CpuParticles2D
        {
            Amount = 10,
            Lifetime = 0.3,
            OneShot = true,
            Explosiveness = 1.0f,
            Direction = normal,
            Spread = 45.0f,
            InitialVelocityMin = 100.0f,
            InitialVelocityMax = 220.0f,
            Gravity = new Vector2(0.0f, 500.0f),
            ScaleAmountMin = 2.0f,
            ScaleAmountMax = 5.0f,
            Color = new Color(1.0f, 0.55f, 0.15f, 0.9f)
        };
        GetTree().CurrentScene.AddChild(impact);
        impact.GlobalPosition = GlobalPosition + normal * 6.0f;
        impact.Emitting = true;
        GetTree().CreateTimer(impact.Lifetime).Timeout += impact.QueueFree;
        QueueFree();
    }
}
