using Godot;

public partial class DamageOverlay : Control
{
    private Tween? _fadeTween;

    public void ShowDamage(int playerId = 0)
    {
        if (_fadeTween != null && _fadeTween.IsValid())
            _fadeTween.Kill();

        var color = Modulate;
        color.A = 1.0f;
        Modulate = color;
        _fadeTween = CreateTween();
        _fadeTween.TweenProperty(this, "modulate:a", 0.0f, 0.35);
    }
}
