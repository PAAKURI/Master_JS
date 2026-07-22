using System;
using System.Collections.Generic;
using System.IO;
using Godot;

public static class NetworkProtocol
{
    public const int ProtocolVersion = 3;
    public const int MapCatalogVersion = 1;
    public const byte InputPacketVersion = 1;
    public const int MaxInputFramesPerPacket = 8;

    [Flags]
    private enum InputButtons : byte
    {
        Jump = 1 << 0,
        Down = 1 << 1,
        Up = 1 << 2,
        Shoot = 1 << 3,
        Parry = 1 << 4
    }

    private const InputButtons AllButtons = InputButtons.Jump | InputButtons.Down | InputButtons.Up | InputButtons.Shoot | InputButtons.Parry;

    public static byte[] EncodeInputPacket(IReadOnlyList<PlayerCommand> commands)
    {
        var count = Math.Min(commands.Count, MaxInputFramesPerPacket);
        using var stream = new MemoryStream(2 + count * 22);
        using var writer = new BinaryWriter(stream);
        writer.Write(InputPacketVersion);
        writer.Write((byte)count);
        for (var index = commands.Count - count; index < commands.Count; index++)
        {
            var command = commands[index].Sanitized();
            writer.Write(command.Sequence);
            writer.Write(command.ClientTick);
            writer.Write(command.Move);
            writer.Write((byte)ToButtons(command));
            writer.Write(command.Aim.X);
            writer.Write(command.Aim.Y);
        }
        return stream.ToArray();
    }

    public static bool TryDecodeInputPacket(byte[] payload, out List<PlayerCommand> commands)
    {
        commands = new List<PlayerCommand>();
        if (payload.Length < 2)
            return false;
        try
        {
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != InputPacketVersion)
                return false;
            var count = reader.ReadByte();
            if (count == 0 || count > MaxInputFramesPerPacket || stream.Length - stream.Position != count * 21L)
                return false;
            for (var index = 0; index < count; index++)
            {
                var sequence = reader.ReadUInt32();
                var tick = reader.ReadUInt32();
                var move = reader.ReadSingle();
                var buttons = (InputButtons)reader.ReadByte();
                var aim = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                if (!float.IsFinite(move) || !aim.IsFinite() || (buttons & ~AllButtons) != 0)
                    return false;
                commands.Add(new PlayerCommand(
                    move,
                    buttons.HasFlag(InputButtons.Jump),
                    buttons.HasFlag(InputButtons.Down),
                    buttons.HasFlag(InputButtons.Up),
                    buttons.HasFlag(InputButtons.Shoot),
                    buttons.HasFlag(InputButtons.Parry),
                    aim,
                    sequence,
                    tick).Sanitized());
            }
            return true;
        }
        catch (EndOfStreamException)
        {
            commands.Clear();
            return false;
        }
    }

    public static bool IsNewer(uint candidate, uint current) => unchecked((int)(candidate - current)) > 0;

    public static bool SelfCheck(out string error)
    {
        var original = new List<PlayerCommand>
        {
            new(-1.0f, true, false, true, true, true, new Vector2(3.0f, 4.0f), 41, 91),
            new(0.5f, false, true, false, false, false, Vector2.Left, 42, 92)
        };
        if (!TryDecodeInputPacket(EncodeInputPacket(original), out var decoded) || decoded.Count != 2)
        {
            error = "input packet round-trip failed";
            return false;
        }
        if (decoded[0].Sequence != 41 || !decoded[0].Jump || !decoded[0].Parry ||
            decoded[1].Sequence != 42 || !decoded[1].Down || !Mathf.IsEqualApprox(decoded[1].Move, 0.5f))
        {
            error = "input packet fields changed during round-trip";
            return false;
        }
        var invalid = EncodeInputPacket(original);
        invalid[0] = 255;
        if (TryDecodeInputPacket(invalid, out _) || TryDecodeInputPacket(new byte[] { InputPacketVersion }, out _))
        {
            error = "invalid input packets were accepted";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static InputButtons ToButtons(PlayerCommand command)
    {
        var buttons = (InputButtons)0;
        if (command.Jump) buttons |= InputButtons.Jump;
        if (command.Down) buttons |= InputButtons.Down;
        if (command.Up) buttons |= InputButtons.Up;
        if (command.Shoot) buttons |= InputButtons.Shoot;
        if (command.Parry) buttons |= InputButtons.Parry;
        return buttons;
    }
}
