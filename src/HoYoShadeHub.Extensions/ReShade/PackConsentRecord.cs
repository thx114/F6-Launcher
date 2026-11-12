using System.Text.Json;

namespace HoYoShadeHub.Extensions.ReShade;

public sealed record PackConsentRecord(string Hash, HashSet<string> Disabled)
{
    public HashSet<string> DisabledSteps { get; init; } = new(StringComparer.Ordinal);
    public PackStepSelection Selection => new(Disabled, DisabledSteps);
    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);

    public static string Serialize(string hash, IEnumerable<string> disabled, IEnumerable<string>? disabledSteps = null)
    {
        string[] names = disabled.ToArray();
        if (!ValidHash(hash) || names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("Invalid pack consent record.");
        if (disabledSteps is null) return JsonSerializer.Serialize(new { schema = 1, hash, disabled = names });
        string[] steps = disabledSteps.ToArray();
        if (steps.Any(string.IsNullOrWhiteSpace) || steps.Distinct(StringComparer.Ordinal).Count() != steps.Length)
            throw new InvalidDataException("Invalid pack step consent record.");
        return JsonSerializer.Serialize(new { schema = 2, hash, disabled = names, disabledSteps = steps });
    }

    public static PackConsentRecord? Parse(string? raw)
    {
        if (raw is null) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var fields = root.EnumerateObject().ToArray();
            if (!root.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out int version) || version is not (1 or 2)) return null;
            int fieldCount = version == 1 ? 3 : 4;
            if (fields.Length != fieldCount || fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fieldCount
                || !root.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String
                || !ValidHash(hash.GetString()!) || !root.TryGetProperty("disabled", out var disabled)
                || disabled.ValueKind != JsonValueKind.Array) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in disabled.EnumerateArray())
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())
                    || !names.Add(item.GetString()!)) return null;
            var steps = new HashSet<string>(StringComparer.Ordinal);
            if (version == 2)
            {
                if (!root.TryGetProperty("disabledSteps", out var disabledSteps) || disabledSteps.ValueKind != JsonValueKind.Array) return null;
                foreach (var item in disabledSteps.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())
                        || !steps.Add(item.GetString()!)) return null;
            }
            return new(hash.GetString()!, names) { DisabledSteps = steps };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    public static PackConsentRecord? ParseLegacy(string? hash, string? raw)
    {
        // Delimited historical records cannot distinguish a literal pipe from multiple names.
        if (hash is null || !ValidHash(hash) || raw is null || !raw.StartsWith(hash + ":", StringComparison.Ordinal)) return null;
        string name = raw[(hash.Length + 1)..];
        if (name.Contains('|') || (name.Length > 0 && string.IsNullOrWhiteSpace(name))) return null;
        return new(hash, name.Length == 0 ? new(StringComparer.Ordinal) : new(new[] { name }, StringComparer.Ordinal));
    }
}
