namespace JasperFx;

/// <summary>
///     How much a Critter Stack tool is allowed to change database schema objects (or other
///     resources) to match the configured model.
/// </summary>
/// <remarks>
///     The behaviour described on each member is Weasel's, which is what actually executes. It is
///     worth reading past the member names: <see cref="CreateOrUpdate" /> is not purely additive,
///     and <see cref="None" /> does not throw. See jasperfx#873.
/// </remarks>
public enum AutoCreate
{
    /// <summary>
    ///     Will drop and recreate tables or other resources that do not match the system configuration or create new ones
    /// </summary>
    All,

    /// <summary>
    ///     Creates missing objects and applies incremental updates to objects in the model, including dropping
    ///     columns, indexes and foreign keys the model no longer declares. Never drops or recreates whole objects,
    ///     and never touches objects the model does not know about.
    /// </summary>
    CreateOrUpdate,

    /// <summary>
    ///     Will create missing schema objects at runtime, but will not update or remove existing schema objects or other resources
    /// </summary>
    CreateOnly,

    /// <summary>
    ///     Makes no schema changes at runtime; a missing object fails with the provider's own error. Explicit apply
    ///     operations (db-apply, resources setup, ApplyAllConfiguredChangesToDatabaseAsync) still migrate as
    ///     CreateOrUpdate. Drift is reported only by AssertDatabaseMatchesConfigurationAsync / db-assert.
    /// </summary>
    None
}
