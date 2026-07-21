using Godot;

public partial class CameraShake : Camera2D
{
    private float _shakeTime;
    private float _shakeStrength;

    public void shake(float strength, float duration)
    {
        _shakeStrength = Mathf.Max(_shakeStrength, strength);
        _shakeTime = Mathf.Max(_shakeTime, duration);
    }

    public override void _Process(double delta)
    {
        _shakeTime = Mathf.Max(_shakeTime - (float)delta, 0.0f);
        if (_shakeTime > 0.0f)
        {
            Offset = new Vector2(
                (float)GD.RandRange(-_shakeStrength, _shakeStrength),
                (float)GD.RandRange(-_shakeStrength, _shakeStrength)
            );
            return;
        }

        Offset = Vector2.Zero;
        _shakeStrength = 0.0f;
    }
}
