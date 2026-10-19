using System.Text.RegularExpressions;

namespace Sandbox.Shared;

/// <summary>
/// The PCI marker that stands in for a raw bank account number: <c>{{pci:ba:TOKEN}}</c>, where TOKEN is <c>Y</c> plus
/// 15 ASCII letters or digits. In a URL the marker is written percent-encoded.
/// </summary>
public static partial class PciMarker
{
    public static string ForBankAccount(string token) => $"{{{{pci:ba:{token}}}}}";

    /// <summary>The marker as it goes into a URL: <c>%7B%7Bpci%3Aba%3A&lt;token&gt;%7D%7D</c>.</summary>
    public static string ForUrl(string token) => Uri.EscapeDataString(ForBankAccount(token));

    public static bool IsTokenShape(string token) => TokenShape().IsMatch(token);

    /// <summary>
    /// True when the text holds anything that starts like a marker: <c>{{pci:</c> in which any character may be a <c>%XX</c>
    /// escape (hex digits in either case), so <c>%7B%7Bpci:</c>, which is how .NET writes a URL with a marker in it, counts.
    /// </summary>
    public static bool ContainsMarker(string text) => MarkerStart().IsMatch(text);

    // \z, not $: a token followed by a line feed is not a token.
    [GeneratedRegex(@"^Y[A-Za-z0-9]{15}\z")]
    private static partial Regex TokenShape();

    [GeneratedRegex(@"(?:\{|%7[Bb]){2}(?:p|%70)(?:c|%63)(?:i|%69)(?::|%3[Aa])")]
    private static partial Regex MarkerStart();
}
