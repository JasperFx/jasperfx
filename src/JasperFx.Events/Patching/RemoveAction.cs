namespace JasperFx.Events;

/// <summary>
/// Controls the behavior when removing elements from a child collection during a patch.
/// </summary>
/// <remarks>
/// Lifted from the near-identical <c>RemoveAction</c> enums in Marten (<c>Marten.Patching</c>)
/// and Polecat (<c>Polecat.Patching</c>); both ordered <c>RemoveFirst=0</c>, <c>RemoveAll=1</c>.
/// Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public enum RemoveAction
{
    /// <summary>
    ///     Remove the first occurrence.
    /// </summary>
    RemoveFirst,

    /// <summary>
    ///     Remove all occurrences.
    /// </summary>
    RemoveAll
}
