using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace GenmitsuControl.Updates;

/// <summary>SemVer precedence, including numeric development build identifiers.</summary>
public sealed partial class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly BigInteger[] _numbers;
    private readonly string[] _prerelease;
    private ReleaseVersion(string text, BigInteger[] numbers, string[] prerelease)
        => (Text, _numbers, _prerelease) = (text, numbers, prerelease);
    public string Text { get; }
    public bool IsPrerelease => _prerelease.Length != 0;
    public bool IsDevelopment => _prerelease is ["dev", _, _] &&
        _prerelease.Skip(1).All(x => x.All(char.IsAsciiDigit));

    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160) return false;
        var text = value.StartsWith('v') ? value[1..] : value;
        var match = VersionPattern().Match(text);
        if (!match.Success) return false;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(x => x.Length > 1 && x.All(char.IsAsciiDigit) && x[0] == '0')) return false;
        version = new ReleaseVersion(text,
            Enumerable.Range(1, 3).Select(i => BigInteger.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray(), prerelease);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++)
        {
            var result = _numbers[i].CompareTo(other._numbers[i]);
            if (result != 0) return result;
        }
        if (!IsPrerelease || !other.IsPrerelease)
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var leftNumeric = BigInteger.TryParse(_prerelease[i], NumberStyles.None, CultureInfo.InvariantCulture, out var left);
            var rightNumeric = BigInteger.TryParse(other._prerelease[i], NumberStyles.None, CultureInfo.InvariantCulture, out var right);
            var result = leftNumeric && rightNumeric ? left.CompareTo(right)
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1
                : string.CompareOrdinal(_prerelease[i], other._prerelease[i]);
            if (result != 0) return result;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex VersionPattern();
}
