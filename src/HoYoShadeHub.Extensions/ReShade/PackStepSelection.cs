using System.Globalization;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>Consent is tied to auto.json's hash and the original step positions, never a filtered/reindexed action.</summary>
public sealed record PackStepSelection(HashSet<string> DisabledActions, HashSet<string> DisabledSteps)
{
    public static string Key(PackAutoAction action, int index) =>
        $"step:{action.Name.Length}:{action.Name}:{index.ToString(CultureInfo.InvariantCulture)}";

    public bool IsEnabled(PackAutoAction action, int index) =>
        !DisabledActions.Contains(action.Name) && !DisabledSteps.Contains(Key(action, index));

    public bool HasEnabledSteps(PackAutoAction action) =>
        Enumerable.Range(0, action.Steps.Count).Any(index => IsEnabled(action, index));

    // Only used to inspect prerequisites. Execution always keeps the full captured action and original indices.
    public IEnumerable<PackActionStep> EnabledSteps(PackAutoAction action) =>
        action.Steps.Where((_, index) => IsEnabled(action, index));
}
