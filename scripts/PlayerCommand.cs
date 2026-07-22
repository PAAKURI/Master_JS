using Godot;

public readonly record struct PlayerCommand(
    float Move,
    bool Jump,
    bool Down,
    bool Up,
    bool Shoot,
    bool Parry,
    Vector2 Aim,
    uint Sequence = 0,
    uint ClientTick = 0,
    float LatencyCompensation = 0.0f)
{
    public static PlayerCommand Neutral => new(0.0f, false, false, false, false, false, Vector2.Right);

    public PlayerCommand Sanitized()
    {
        var safeAim = Aim.IsFinite() && Aim.LengthSquared() > 0.0001f ? Aim.Normalized() : Vector2.Right;
        var safeMove = float.IsFinite(Move) ? Mathf.Clamp(Move, -1.0f, 1.0f) : 0.0f;
        return this with { Move = safeMove, Aim = safeAim };
    }
}
