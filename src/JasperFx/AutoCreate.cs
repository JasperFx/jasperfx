namespace JasperFx;

public enum AutoCreate
{
    /// <summary>
    ///     Will drop and recreate tables or other resources that do not match the system configuration or create new ones
    /// </summary>
    All,

    /// <summary>
    ///     Will never destroy existing tables or other resources. Attempts to add missing columns or missing tables or other additive changes
    /// </summary>
    CreateOrUpdate,

    /// <summary>
    ///     Will create missing schema objects at runtime, but will not update or remove existing schema objects or other resources
    /// </summary>
    CreateOnly,

    /// <summary>
    ///     Do not recreate, destroy, or update schema objects or other resources at runtime. Will throw exceptions if
    ///     the schema does not match the system configuration
    /// </summary>
    None
}