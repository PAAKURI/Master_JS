using Godot;

public partial class Bullet : RigidBody2D
{
    private const int MaxTrailPoints = 30;
    private const float LifetimeSeconds = 5.0f;
    private const int Damage = 25;
    private const float OwnerCollisionGrace = 0.15f;

    private Line2D _trail = null!;
    private float _age;
    private bool _exploded;
    private PhysicsBody2D? _ownerPlayer;
    private int _ownerVersion;

    public override void _Ready()
    {
        _trail = GetNode<Line2D>("Trail");
    }

    public void SetOwnerPlayer(PhysicsBody2D player)
    {
        _ownerVersion++;
        if (GodotObject.IsInstanceValid(_ownerPlayer))
            RemoveCollisionExceptionWith(_ownerPlayer!);

        _ownerPlayer = player;
        AddCollisionExceptionWith(player);
        var ignoredPlayer = player;
        var version = _ownerVersion;
        GetTree().CreateTimer(OwnerCollisionGrace).Timeout += () =>
        {
            if (version == _ownerVersion && GodotObject.IsInstanceValid(ignoredPlayer))
                RemoveCollisionExceptionWith(ignoredPlayer);
        };
    }

    public bool Parry(PhysicsBody2D player, Vector2 incomingVelocity)
    {
        SetOwnerPlayer(player);
        _exploded = false;
        Sleeping = false;
        var reflectedVelocity = -incomingVelocity * 1.15f;
        ApplyCentralImpulse((reflectedVelocity - LinearVelocity) * Mass);
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

        _trail.AddPoint(GlobalPosition);
        if (_trail.GetPointCount() > MaxTrailPoints)
            _trail.RemovePoint(0);
    }

    public override void _IntegrateForces(PhysicsDirectBodyState2D state)
    {
        if (_exploded || state.GetContactCount() == 0)
            return;

        _exploded = true;
        var incomingVelocity = state.LinearVelocity;
        state.LinearVelocity = Vector2.Zero;
        var normal = state.GetContactLocalNormal(0).Normalized();
        var collider = state.GetContactColliderObject(0);
        Callable.From(() => ResolveCollision(collider, normal, incomingVelocity)).CallDeferred();
    }

    private void ResolveCollision(GodotObject collider, Vector2 normal, Vector2 incomingVelocity)
    {
        if (GodotObject.IsInstanceValid(collider) && collider is Player player && player.TryParry(this, incomingVelocity))
            return;
        if (GodotObject.IsInstanceValid(collider) && collider is Player target)
        {
            target.TakeDamage(Damage);
            QueueFree();
            return;
        }

        SpawnImpact(normal);
    }

    private void SpawnImpact(Vector2 normal)
    {
        GetTree().CallGroup("camera_shake", "shake", 5.0f, 0.12f);
        var impact = new CpuParticles2D
        {
            Amount = 12,
            Lifetime = 0.35,
            OneShot = true,
            Explosiveness = 1.0f,
            Direction = normal,
            Spread = 50.0f,
            InitialVelocityMin = 120.0f,
            InitialVelocityMax = 260.0f,
            Gravity = new Vector2(0.0f, 500.0f),
            ScaleAmountMin = 3.0f,
            ScaleAmountMax = 7.0f,
            Color = new Color(1.0f, 0.55f, 0.15f, 0.9f)
        };
        GetTree().CurrentScene.AddChild(impact);
        impact.GlobalPosition = GlobalPosition + normal * 6.0f;
        impact.Emitting = true;
        GetTree().CreateTimer(impact.Lifetime).Timeout += impact.QueueFree;
        QueueFree();
    }
}
