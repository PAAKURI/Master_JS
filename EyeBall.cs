using Godot;

public partial class EyeBall : RigidBody2D
{
    [Export] public Vector2 Anchor { get; set; }
    [Export] public float SpringStrength { get; set; } = 58.0f;
    [Export] public float Damping { get; set; } = 7.5f;
    [Export] public float Chaos { get; set; } = 85.0f;

    private Node2D _target = null!;
    private double _phase;

    public override void _Ready()
    {
        _target = GetParent<Node2D>();
        var start = _target.ToGlobal(Anchor);
        TopLevel = true;
        GlobalPosition = start;
        GravityScale = 0.0f;
        CollisionLayer = 0;
        CollisionMask = 0;
        LockRotation = true;
        CanSleep = false;
        LinearDamp = 0.4f;
        _phase = GD.RandRange(0.0, Mathf.Tau);
    }

    public override void _IntegrateForces(PhysicsDirectBodyState2D state)
    {
        if (!GodotObject.IsInstanceValid(_target))
            return;
        _phase += state.Step * 17.0;
        var targetPosition = _target.ToGlobal(Anchor);
        var offset = targetPosition - state.Transform.Origin;
        var noise = new Vector2(Mathf.Sin((float)_phase), Mathf.Cos((float)(_phase * 1.37))) * Chaos;
        state.LinearVelocity += (offset * SpringStrength - state.LinearVelocity * Damping + noise) * state.Step;
        if (offset.Length() > 28.0f)
        {
            var transform = state.Transform;
            transform.Origin = targetPosition - offset.Normalized() * 28.0f;
            state.Transform = transform;
        }
    }

    public void SnapToAnchor()
    {
        if (!GodotObject.IsInstanceValid(_target))
            return;
        GlobalPosition = _target.ToGlobal(Anchor);
        LinearVelocity = Vector2.Zero;
    }
}
