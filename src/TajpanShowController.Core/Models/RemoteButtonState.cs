namespace TajpanShowController.Core.Models;

public readonly record struct RemoteButtonState(
    bool Start, bool Stop, bool Pause, bool Previous, bool Next,
    bool Reserved1, bool Reserved2, bool Reserved3)
{
    public static RemoteButtonState FromBits(string bits)
    {
        if (bits.Length != 8 || bits.Any(c => c is not ('0' or '1')))
            throw new FormatException("A gombállapotnak pontosan nyolc bináris karaktert kell tartalmaznia.");
        return new(bits[0] == '1', bits[1] == '1', bits[2] == '1', bits[3] == '1',
            bits[4] == '1', bits[5] == '1', bits[6] == '1', bits[7] == '1');
    }
}
