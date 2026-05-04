namespace JasperFx.Descriptors;

/// <summary>
/// Diagnostic snapshot of a Critter Stack store's code-generation settings —
/// the values resolved at runtime from <c>StoreOptions</c>'s code-gen properties
/// (most of which are <c>[Obsolete]</c> in favour of <c>CritterStackDefaults()</c>
/// but remain operationally interesting). Intentionally a parallel descriptor
/// type rather than a flat OptionValue bag so the surface stays cohesive even
/// while the underlying StoreOptions properties are deprecated.
/// </summary>
public class CodeGenerationDescriptor
{
    /// <summary>
    /// Identity of the application assembly the store scanned for generated-code
    /// types — typically <c>Assembly.GetEntryAssembly().GetName().FullName</c>.
    /// Null when not configured (the runtime falls back to entry assembly).
    /// </summary>
    public string? ApplicationAssembly { get; set; }

    /// <summary>
    /// Whether Marten / Polecat writes generated <c>.cs</c> files to disk on first
    /// activation. Defaults to <c>true</c>.
    /// </summary>
    public bool SourceCodeWritingEnabled { get; set; }

    /// <summary>
    /// Resolved output directory for generated source files. Null when defaulted
    /// (the runtime uses <c>{ContentRoot}/Internal/Generated</c>).
    /// </summary>
    public string? GeneratedCodeOutputPath { get; set; }

    /// <summary>
    /// Whether the store dynamically generates code at startup
    /// (<c>"Dynamic"</c>) or loads pre-built types from the entry assembly
    /// (<c>"Static"</c> / <c>"Auto"</c>). Mirrors <c>JasperFx.CodeGeneration.TypeLoadMode</c>
    /// as a string so descriptors remain serializer-friendly across version skews.
    /// </summary>
    public string GeneratedCodeMode { get; set; } = "";
}
