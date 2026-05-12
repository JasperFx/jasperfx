using JasperFx.CodeGeneration.Snapshots;
using Shouldly;

namespace CodegenTests.Snapshots;

/// <summary>
///     Phase 1 of jasperfx#243 — the invalidation contract shared across
///     codegen consumers (Marten, Wolverine, Polecat, etc.). These tests
///     pin the public surface: persistence round-trip, the three-way verdict
///     matrix, hash determinism, and the soft-fallback semantics around
///     malformed / missing fingerprints.
/// </summary>
public class SnapshotGateTests : IDisposable
{
    private readonly string _folder;

    public SnapshotGateTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "jasperfx-snapshot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static SnapshotFingerprint NewFingerprint(string configHash = "deadbeef") =>
        new(
            ProductName: "test-product",
            ProductVersion: "1.2.3",
            JasperFxVersion: "2.0.0-alpha.8",
            ConfigHash: configHash);

    // ─── Read / Write round-trip ─────────────────────────────────────────

    [Fact]
    public void read_returns_null_when_folder_has_no_fingerprint()
    {
        SnapshotGate.Read(_folder).ShouldBeNull();
    }

    [Fact]
    public void read_returns_null_when_folder_path_is_empty()
    {
        SnapshotGate.Read("").ShouldBeNull();
        SnapshotGate.Read("   ").ShouldBeNull();
    }

    [Fact]
    public void write_then_read_round_trips_every_field()
    {
        var written = NewFingerprint();
        SnapshotGate.Write(_folder, written);

        var read = SnapshotGate.Read(_folder);

        read.ShouldNotBeNull();
        read.ShouldBe(written);
    }

    [Fact]
    public void write_creates_the_target_folder_if_it_does_not_exist()
    {
        var nested = Path.Combine(_folder, "nested", "subfolder");
        SnapshotGate.Write(nested, NewFingerprint());

        File.Exists(Path.Combine(nested, SnapshotGate.FingerprintFileName)).ShouldBeTrue();
    }

    [Fact]
    public void write_overwrites_a_prior_fingerprint()
    {
        SnapshotGate.Write(_folder, NewFingerprint("first"));
        SnapshotGate.Write(_folder, NewFingerprint("second"));

        SnapshotGate.Read(_folder)!.ConfigHash.ShouldBe("second");
    }

    [Fact]
    public void read_returns_null_when_persisted_file_is_malformed()
    {
        // Soft-fallback policy: an unreadable fingerprint is indistinguishable
        // from "no fingerprint" for the consumer's purposes. Both produce
        // FirstBoot, both regenerate, neither throws.
        File.WriteAllText(
            Path.Combine(_folder, SnapshotGate.FingerprintFileName),
            "{ this is not valid json ");

        SnapshotGate.Read(_folder).ShouldBeNull();
    }

    [Fact]
    public void write_with_null_fingerprint_throws()
    {
        Should.Throw<ArgumentNullException>(() => SnapshotGate.Write(_folder, null!));
    }

    [Fact]
    public void write_with_empty_folder_throws()
    {
        Should.Throw<ArgumentException>(() => SnapshotGate.Write("", NewFingerprint()));
    }

    // ─── Verify ──────────────────────────────────────────────────────────

    [Fact]
    public void verify_returns_FirstBoot_when_persisted_is_null()
    {
        SnapshotGate.Verify(NewFingerprint(), persisted: null)
            .ShouldBe(SnapshotVerdict.FirstBoot);
    }

    [Fact]
    public void verify_returns_Accept_when_every_field_matches()
    {
        var live = NewFingerprint();
        var persisted = NewFingerprint();

        SnapshotGate.Verify(live, persisted).ShouldBe(SnapshotVerdict.Accept);
    }

    [Theory]
    [InlineData("product")]
    [InlineData("version")]
    [InlineData("jasperfx")]
    [InlineData("config")]
    [InlineData("schema")]
    public void verify_returns_RejectAndRegenerate_when_any_field_differs(string differingField)
    {
        var live = NewFingerprint();
        var persisted = differingField switch
        {
            "product" => live with { ProductName = "other-product" },
            "version" => live with { ProductVersion = "9.9.9" },
            "jasperfx" => live with { JasperFxVersion = "99.0.0" },
            "config" => live with { ConfigHash = "0000000000000000" },
            "schema" => live with { SchemaVersion = SnapshotGate.CurrentSchemaVersion + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(differingField))
        };

        SnapshotGate.Verify(live, persisted).ShouldBe(SnapshotVerdict.RejectAndRegenerate);
    }

    [Fact]
    public void verify_with_null_live_throws()
    {
        Should.Throw<ArgumentNullException>(() => SnapshotGate.Verify(null!, NewFingerprint()));
    }

    // ─── ComputeHash ─────────────────────────────────────────────────────

    [Fact]
    public void compute_hash_is_deterministic()
    {
        SnapshotGate.ComputeHash("the same input")
            .ShouldBe(SnapshotGate.ComputeHash("the same input"));
    }

    [Fact]
    public void compute_hash_differs_for_different_inputs()
    {
        SnapshotGate.ComputeHash("input A")
            .ShouldNotBe(SnapshotGate.ComputeHash("input B"));
    }

    [Fact]
    public void compute_hash_returns_lowercase_hex_digest_of_expected_length()
    {
        // SHA-256 = 256 bits = 64 hex chars
        var hash = SnapshotGate.ComputeHash("anything");

        hash.Length.ShouldBe(64);
        hash.ShouldAllBe(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    [Fact]
    public void compute_hash_with_null_input_throws()
    {
        Should.Throw<ArgumentNullException>(() => SnapshotGate.ComputeHash(null!));
    }

    // ─── Stability promises ──────────────────────────────────────────────

    [Fact]
    public void log_template_includes_documented_placeholders()
    {
        // The template is part of the public stability contract (per the
        // jasperfx#243 design). Operators grep for it in logs to recognise
        // the "snapshot rejected" path. If this test fails, you're about to
        // break someone's log-monitoring rule — bump JasperFx to 3.0 first.
        SnapshotGate.SnapshotRejectedLogTemplate.ShouldContain("{ProductName}");
        SnapshotGate.SnapshotRejectedLogTemplate.ShouldContain("{Namespace}");
        SnapshotGate.SnapshotRejectedLogTemplate.ShouldContain("{Reason}");
        SnapshotGate.SnapshotRejectedLogTemplate.ShouldStartWith("JasperFx.Codegen: snapshot rejected");
    }

    [Fact]
    public void fingerprint_filename_is_stable()
    {
        // Same stability promise as the log template — consumers and their
        // build tooling key off this constant.
        SnapshotGate.FingerprintFileName.ShouldBe("fingerprint.json");
    }

    [Fact]
    public void schema_version_default_is_current()
    {
        new SnapshotFingerprint("p", "v", "j", "h")
            .SchemaVersion.ShouldBe(SnapshotGate.CurrentSchemaVersion);
    }
}
