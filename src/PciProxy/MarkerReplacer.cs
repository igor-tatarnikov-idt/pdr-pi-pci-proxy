using System.Buffers;
using System.Text;
using Sandbox.Shared;

namespace PciProxy;

/// <summary>
/// Finds and replaces PCI markers in bytes. A marker is <c>{{pci:ba:TOKEN}}</c> in which any character may be written as
/// a <c>%XX</c> escape (hex digits in either case), so <c>%7B%7Bpci%3Aba%3ATOKEN%7D%7D</c> (what <c>Uri.EscapeDataString</c> writes)
/// and <c>%7B%7Bpci:ba:TOKEN%7D%7D</c> (what .NET puts on the wire for a URL written with <c>{{ }}</c>) are markers too. A marker
/// is found wherever it stands. Anything that starts like a marker (<c>{{pci:</c> so written) but is not a well-formed one is
/// <c>MARKER_SYNTAX</c>, so no half-marker can reach a provider. Bytes outside the markers are never touched, so a body does not
/// have to be UTF-8.
/// </summary>
public static class MarkerReplacer
{
    private const int TokenLength = 16;

    private readonly record struct Marker(int Start, int Length, string Token);

    /// <summary>The distinct tokens of all the markers in the input. Throws <c>MARKER_SYNTAX</c> for a malformed marker.</summary>
    public static IReadOnlyCollection<string> FindTokens(ReadOnlySpan<byte> input)
    {
        var tokens = new HashSet<string>();
        var from = 0;
        while (TryFindNext(input, from, out var marker))
        {
            tokens.Add(marker.Token);
            from = marker.Start + marker.Length;
        }
        return tokens;
    }

    /// <summary>
    /// The input with every marker replaced by the raw value of its token, percent-encoded when <paramref name="percentEncode"/>
    /// is set (for a URL) and as it is otherwise. Throws <c>MARKER_SYNTAX</c> for a malformed marker.
    /// </summary>
    public static byte[] Replace(ReadOnlySpan<byte> input, IReadOnlyDictionary<string, string> rawByToken, bool percentEncode)
    {
        var output = new ArrayBufferWriter<byte>(Math.Max(input.Length, 16));
        var copied = 0;
        while (TryFindNext(input, copied, out var marker))
        {
            if (!rawByToken.TryGetValue(marker.Token, out var raw))
            {
                throw ProxyException.Error("A marker has no value to replace it with.");
            }
            output.Write(input[copied..marker.Start]);
            output.Write(Encoding.UTF8.GetBytes(percentEncode ? Uri.EscapeDataString(raw) : raw));
            copied = marker.Start + marker.Length;
        }
        output.Write(input[copied..]);
        return output.WrittenSpan.ToArray();
    }

    private static bool TryFindNext(ReadOnlySpan<byte> input, int from, out Marker marker)
    {
        // A marker starts with '{' or with the escape '%7B'; everything else is skipped.
        for (var start = from; start < input.Length; start++)
        {
            var offset = input[start..].IndexOfAny((byte)'{', (byte)'%');
            if (offset < 0)
            {
                break;
            }
            start += offset;
            if (TryParse(input, start, out marker))
            {
                return true;
            }
        }
        marker = default;
        return false;
    }

    // False when no marker starts at 'start'; throws when one starts there and is malformed.
    private static bool TryParse(ReadOnlySpan<byte> input, int start, out Marker marker)
    {
        var position = start;
        if (!Consume(input, ref position, "{{pci:"u8))
        {
            marker = default;
            return false;
        }
        if (!Consume(input, ref position, "ba:"u8) || !TryReadToken(input, ref position, out var token) || !Consume(input, ref position, "}}"u8))
        {
            throw ProxyException.MarkerSyntax("A PCI marker is malformed: expected {{pci:ba:<token>}}.");
        }
        marker = new Marker(start, position - start, token);
        return true;
    }

    // Moves 'position' past 'expected' when the input has it there, each character written as itself or as a %XX escape.
    private static bool Consume(ReadOnlySpan<byte> input, ref int position, ReadOnlySpan<byte> expected)
    {
        foreach (var b in expected)
        {
            if (!TryReadCharacter(input, ref position, out var actual) || actual != b)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryReadToken(ReadOnlySpan<byte> input, ref int position, out string token)
    {
        token = string.Empty;
        Span<byte> characters = stackalloc byte[TokenLength];
        for (var i = 0; i < TokenLength; i++)
        {
            if (!TryReadCharacter(input, ref position, out characters[i]))
            {
                return false;
            }
        }
        // A byte outside ASCII becomes '?', which is not in the token alphabet.
        var candidate = Encoding.ASCII.GetString(characters);
        if (!PciMarker.IsTokenShape(candidate))
        {
            return false;
        }
        token = candidate;
        return true;
    }

    // Reads one character: the byte at 'position', or the byte that a %XX escape there stands for. A '%' that is not
    // followed by two hex digits is just that byte.
    private static bool TryReadCharacter(ReadOnlySpan<byte> input, ref int position, out byte character)
    {
        if (position >= input.Length)
        {
            character = 0;
            return false;
        }
        if (input[position] == '%' && position + 2 < input.Length && HexValue(input[position + 1]) is >= 0 and var high
            && HexValue(input[position + 2]) is >= 0 and var low)
        {
            character = (byte)(high << 4 | low);
            position += 3;
            return true;
        }
        character = input[position++];
        return true;
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
