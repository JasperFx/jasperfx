using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling.Dcb;

/// <summary>
/// jasperfx#1297: Dynamic Consistency Boundaries on the Event Model — the tag catalogue, per-event tags,
/// declared reads, the derived boundary query, untagged projections and rejection reasons.
/// </summary>
public class DcbBoundaryTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static readonly JsonSerializerOptions CritterWatchWire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    #region sample_tests_consistency_boundary

    public record CourseId(Guid Value);
    public record StudentId(Guid Value);
    public record EnrolStudent(CourseId CourseId, StudentId StudentId);
    public record StudentEnrolled(CourseId CourseId, StudentId StudentId);
    public class CourseEnrolment;
    public class StudentSchedule;
    public class EnrolmentReport;

    public class CourseEnrolments : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model)
        {
            // The tag catalogue: the strong-typed ids events carry and queries select by
            model.Tag<CourseId, Guid>();
            model.Tag<StudentId>();

            model.Command<EnrolStudent>()
                // Reads are declared; the boundary is derived from them
                .Decides<CourseEnrolment>().ByTag<EnrolStudent, CourseId>(x => x.CourseId)
                .Reads<StudentSchedule>().By<EnrolStudent, StudentId>(x => x.StudentId)
                .Emits<StudentEnrolled>().TaggedBy<CourseId>().TaggedBy<StudentId>()
                // A projection the boundary deliberately leaves out
                .UntaggedProjection<EnrolmentReport>()
                // The ways the command can refuse
                .Refuses("Course is full")
                .Refuses("Student already enrolled");
        }
    }

    #endregion

    private static EventModelDescriptor build()
    {
        var model = new EventModelBuilder();
        new CourseEnrolments().Configure(model);
        return model.Build("Enrolments");
    }

    // ---- the catalogue ----

    [Fact]
    public void tags_are_named_in_camel_case_from_their_type()
    {
        EventTagDescriptor.NameFor(typeof(CourseId)).ShouldBe("courseId");
        EventTagDescriptor.NameFor("ID").ShouldBe("iD");
        EventTagDescriptor.NameFor("X").ShouldBe("x");
    }

    [Fact]
    public void the_catalogue_carries_declared_tags_with_type_and_inner_primitive()
    {
        var descriptor = build();

        descriptor.Tags.Select(x => (x.Name, x.Type?.Name, x.InnerType?.Name)).ShouldBe([
            ("courseId", "CourseId", "Guid"),
            ("studentId", "StudentId", null),
        ]);
    }

    [Fact]
    public void a_tag_an_event_is_tagged_by_joins_the_catalogue_on_its_own()
    {
        var model = new EventModelBuilder();
        model.Command<EnrolStudent>()
            .Emits<StudentEnrolled>().TaggedBy<CourseId>().TaggedBy("term");

        var descriptor = model.Build("Enrolments");

        descriptor.Tags.Select(x => (x.Name, x.Type?.Name)).ShouldBe([("courseId", "CourseId"), ("term", null)]);
        descriptor.TagNamed("courseId")!.Type.ShouldBe(T<CourseId>());
    }

    [Fact]
    public void a_tag_declared_by_name_gets_its_type_from_a_later_use()
    {
        var model = new EventModelBuilder();
        model.Tag("courseId", typeof(Guid));
        model.Command<EnrolStudent>().Emits<StudentEnrolled>().TaggedBy<CourseId>();

        var tag = model.Build("Enrolments").Tags.ShouldHaveSingleItem();
        tag.Type.ShouldBe(T<CourseId>());
        tag.InnerType.ShouldBe(T<Guid>());
    }

    [Fact]
    public void declaring_one_tag_name_with_two_types_throws()
    {
        var model = new EventModelBuilder();
        model.Tag("courseId", typeof(Guid)).Tag<CourseId>();           // the type fills in: no conflict

        // Two different types share the simple name, so they would share the tag name
        Should.Throw<InvalidOperationException>(() => model.Tag<Other.CourseId>())
            .Message.ShouldContain("already declared");
    }

    public static class Other
    {
        public record CourseId(string Code);
    }

    // ---- per-event tags ----

    [Fact]
    public void an_emitted_event_records_its_tags()
    {
        var slice = build().Slices.Single();

        slice.EventTags.ShouldBe([new EmittedEventTags(T<StudentEnrolled>(), ["courseId", "studentId"])]);
        slice.TagsFor(T<StudentEnrolled>()).ShouldBe(["courseId", "studentId"]);
        slice.TagsFor(T<EnrolStudent>()).ShouldBeEmpty();
        slice.Claims(EventModelRole.EventTags).ShouldBeTrue();
    }

    [Fact]
    public void tagged_by_needs_an_emitted_event_first()
    {
        Should.Throw<InvalidOperationException>(() => new EventModelBuilder().Command<EnrolStudent>().TaggedBy<CourseId>())
            .Message.ShouldContain("Emits()");
    }

    // ---- reads and the derived boundary ----

    [Fact]
    public void reads_are_declared_and_the_boundary_query_is_derived()
    {
        var slice = build().Slices.Single();

        slice.DeciderModel.ShouldBe(T<CourseEnrolment>());
        slice.AggregateDeclaration.ShouldBe(AggregateDeclaration.DeciderModel);
        slice.ReadsFrom.ShouldBe([T<StudentSchedule>()]);

        slice.Reads.ShouldBe([
            new BoundaryReadDescriptor(BoundaryReadKind.Model, T<CourseEnrolment>(), "courseId", "CourseId"),
            new BoundaryReadDescriptor(BoundaryReadKind.Projection, T<StudentSchedule>(), "studentId", "StudentId"),
        ]);

        var query = slice.BoundaryQuery;
        query.IsEmpty.ShouldBeFalse();
        query.ShouldBe(new BoundaryQueryDescriptor([
            new BoundaryQueryTerm("courseId", "CourseId", [T<StudentEnrolled>()]),
            new BoundaryQueryTerm("studentId", "StudentId", [T<StudentEnrolled>()]),
        ]));
    }

    [Fact]
    public void a_slice_that_reads_nothing_by_tag_has_an_empty_boundary()
    {
        var model = new EventModelBuilder();
        model.Command<EnrolStudent>().Against<CourseEnrolment>().Emits<StudentEnrolled>();

        var slice = model.BuildSlices().Single();
        slice.Reads.ShouldBeEmpty();
        slice.BoundaryQuery.IsEmpty.ShouldBeTrue();
        slice.Claims(EventModelRole.Reads).ShouldBeFalse();
    }

    [Fact]
    public void by_reads_the_member_off_a_lambda_and_refuses_anything_else()
    {
        EventModelSliceBuilder.MemberNameOf((System.Linq.Expressions.Expression<Func<EnrolStudent, object?>>)(x => x.CourseId))
            .ShouldBe("CourseId");

        Should.Throw<ArgumentException>(() =>
            EventModelSliceBuilder.MemberNameOf((System.Linq.Expressions.Expression<Func<EnrolStudent, object?>>)(x => x.ToString())));
    }

    [Fact]
    public void by_without_a_read_before_it_throws()
    {
        Should.Throw<InvalidOperationException>(() => new EventModelBuilder().Command<EnrolStudent>().By<CourseId>())
            .Message.ShouldContain("Reads<T>()");
    }

    [Fact]
    public void reads_work_by_name_before_the_types_exist()
    {
        var model = new EventModelBuilder();
        model.Command("EnrolStudent")
            .Decides("CourseEnrolment").ByTag("courseId", "CourseId")
            .Reads("StudentSchedule").By("studentId")
            .Emits("StudentEnrolled").TaggedBy("courseId");

        var slice = model.BuildSlices().Single();
        slice.Reads.Select(x => $"{x.Kind} {x.Type.Name} by {x.Tag} from {x.Member ?? "(convention)"}").ShouldBe([
            "Model CourseEnrolment by courseId from CourseId",
            "Projection StudentSchedule by studentId from (convention)",
        ]);
        slice.BoundaryQuery.Terms.Select(x => x.EventTypes.Count).ShouldBe([1, 0]);
    }

    [Fact]
    public void the_model_resolves_the_events_a_read_folds()
    {
        var model = new EventModelBuilder();
        new CourseEnrolments().Configure(model);
        model.Aggregate<CourseEnrolment>();
        model.View<StudentSchedule>().From<StudentEnrolled>().From<CourseId>();

        var descriptor = model.Build("Enrolments") with
        {
            Aggregates = [new AggregateDescriptor(T<CourseEnrolment>(), AggregateKind.WriteAggregate, [T<StudentEnrolled>(), T<EnrolStudent>()])],
        };

        var enrol = descriptor.Slices.Single(x => x.Name == nameof(EnrolStudent));
        var resolved = descriptor.BoundaryQueryFor(enrol);

        resolved.Terms.Select(x => $"{x.Tag}: {string.Join(", ", x.EventTypes.Select(e => e.Name))}").ShouldBe([
            // the slice's own tagged event, then the decider model's applied events
            "courseId: StudentEnrolled, EnrolStudent",
            // the slice's own tagged event, then what the StudentSchedule view consumes
            "studentId: StudentEnrolled, CourseId",
        ]);
    }

    // ---- untagged projections and rejection reasons ----

    [Fact]
    public void an_untagged_projection_is_a_projection_too()
    {
        var slice = build().Slices.Single();

        slice.UntaggedProjections.ShouldBe([T<EnrolmentReport>()]);
        slice.ProjectionTypes.ShouldBe([T<EnrolmentReport>()]);
        slice.Claims(EventModelRole.UntaggedProjections).ShouldBeTrue();
    }

    [Fact]
    public void rejection_reasons_are_recorded_once_each()
    {
        var model = new EventModelBuilder();
        model.Command<EnrolStudent>().Refuses("Course is full").Refuses(" Course is full ").Refuses("Student already enrolled");

        model.BuildSlices().Single().RejectionReasons.ShouldBe(["Course is full", "Student already enrolled"]);
    }

    // ---- merge ----

    [Fact]
    public void tags_union_on_the_same_rung()
    {
        var first = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            EmittedEvents = [T<StudentEnrolled>()],
            EventTags = [new EmittedEventTags(T<StudentEnrolled>(), ["courseId"])],
            Provenance = EventModelProvenance.Declared,
        };
        var second = first with { EventTags = [new EmittedEventTags(T<StudentEnrolled>(), ["studentId"])] };

        var merged = first.Merge(second);

        merged.TagsFor(T<StudentEnrolled>()).ShouldBe(["courseId", "studentId"]);
        merged.Hotspots.ShouldBeEmpty();
        merged.ProvenanceFor(EventModelRole.EventTags).ShouldBe(EventModelProvenance.Declared);
    }

    [Fact]
    public void a_tag_disagreement_on_an_event_is_recorded_like_an_aggregate_identity()
    {
        var declared = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            EmittedEvents = [T<StudentEnrolled>()],
            EventTags = [new EmittedEventTags(T<StudentEnrolled>(), ["courseId", "studentId"])],
            Provenance = EventModelProvenance.Declared,
        };
        var derived = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            EmittedEvents = [T<StudentEnrolled>()],
            EventTags = [new EmittedEventTags(T<StudentEnrolled>(), ["courseId"])],
            Provenance = EventModelProvenance.Derived,
        };

        foreach (var merged in new[] { declared.Merge(derived), derived.Merge(declared) })
        {
            // The code wins the tags on the event it emits...
            merged.TagsFor(T<StudentEnrolled>()).ShouldBe(["courseId"]);
            merged.ProvenanceFor(EventModelRole.EventTags).ShouldBe(EventModelProvenance.Derived);

            // ...and the declaration's extra tag is recorded, naming the event
            var hotspot = merged.Hotspots.ShouldHaveSingleItem();
            hotspot.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
            hotspot.Role.ShouldBe(EventModelRole.EventTags);
            hotspot.WinningClaim!.Value.ShouldBe("StudentEnrolled tagged courseId");
            hotspot.LosingClaim!.Value.ShouldBe("StudentEnrolled tagged courseId, studentId");
        }
    }

    [Fact]
    public void declared_tags_survive_the_merge_with_code_that_knows_no_tags()
    {
        var declared = build().Slices.Single().WithProvenance(EventModelProvenance.Declared);
        var derived = EventModelSliceDescriptor.Named(nameof(EnrolStudent)) with
        {
            EmittedEvents = [T<StudentEnrolled>()],
            CommandType = T<EnrolStudent>(),
            Provenance = EventModelProvenance.Derived,
        };

        var merged = derived.Merge(declared);

        merged.TagsFor(T<StudentEnrolled>()).ShouldBe(["courseId", "studentId"]);
        merged.Reads.Count.ShouldBe(2);
        merged.RejectionReasons.ShouldBe(["Course is full", "Student already enrolled"]);
        merged.Hotspots.ShouldBeEmpty();
    }

    [Fact]
    public void tags_on_an_event_the_code_does_not_emit_go_with_it()
    {
        var declared = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            EmittedEvents = [T<StudentEnrolled>(), T<EnrolStudent>()],
            EventTags = [new EmittedEventTags(T<EnrolStudent>(), ["courseId"])],
            Provenance = EventModelProvenance.Declared,
        };
        var derived = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            EmittedEvents = [T<StudentEnrolled>()],
            Provenance = EventModelProvenance.Derived,
        };

        declared.Merge(derived).EventTags.ShouldBeEmpty();
    }

    [Fact]
    public void the_catalogue_unions_by_name_and_a_real_type_replaces_a_declaration()
    {
        var declared = new EventModelDescriptor("Enrolments", [])
        {
            Tags = [new EventTagDescriptor("courseId", null) { InnerType = T<Guid>() }, new EventTagDescriptor("term", null)],
        }.WithProvenance(EventModelProvenance.Declared);
        var derived = new EventModelDescriptor("Enrolments", [])
        {
            Tags = [new EventTagDescriptor("courseId", T<CourseId>())],
        }.WithProvenance(EventModelProvenance.Derived);

        var merged = EventModelDescriptor.Merge("Enrolments", [declared, derived]);

        merged.Tags.Select(x => (x.Name, x.Type?.Name, x.InnerType?.Name, x.Provenance)).ShouldBe([
            ("courseId", "CourseId", "Guid", EventModelProvenance.Derived),
            ("term", null, null, EventModelProvenance.Declared),
        ]);
        merged.Hotspots.ShouldBeEmpty();
    }

    [Fact]
    public void two_types_for_one_tag_leave_a_model_level_disagreement_on_tag_type()
    {
        var declared = new EventModelDescriptor("Enrolments", [])
        {
            Tags = [new EventTagDescriptor("courseId", T<CourseId>())],
        }.WithProvenance(EventModelProvenance.Declared);
        var derived = new EventModelDescriptor("Enrolments", [])
        {
            Tags = [new EventTagDescriptor("courseId", T<StudentId>())],
        }.WithProvenance(EventModelProvenance.Derived);

        var merged = EventModelDescriptor.Merge("Enrolments", [declared, derived]);

        merged.Tags.ShouldHaveSingleItem().Type.ShouldBe(T<StudentId>());
        var hotspot = merged.Hotspots.ShouldHaveSingleItem();
        hotspot.Role.ShouldBe(EventModelRole.TagType);
        hotspot.Text.ShouldBe("TagType of courseId: Derived claims StudentId; Declared claims CourseId");
    }

    [Fact]
    public void rejection_reasons_union_across_rungs()
    {
        var declared = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            RejectionReasons = ["Course is full"],
            Provenance = EventModelProvenance.Declared,
        };
        var specified = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            RejectionReasons = ["Course is full"],
            Provenance = EventModelProvenance.Specified,
        };

        var merged = declared.Merge(specified);
        merged.RejectionReasons.ShouldBe(["Course is full"]);
        merged.Hotspots.ShouldBeEmpty();
        merged.ProvenanceFor(EventModelRole.RejectionReasons).ShouldBe(EventModelProvenance.Specified);
    }

    [Fact]
    public void a_spec_refusing_for_an_undeclared_reason_is_a_hotspot()
    {
        var declared = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            RejectionReasons = ["Course is full"],
            Provenance = EventModelProvenance.Declared,
        };
        var specified = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            RejectionReasons = ["Course is full", "Student already enrolled"],
            Provenance = EventModelProvenance.Specified,
        };

        foreach (var merged in new[] { declared.Merge(specified), specified.Merge(declared) })
        {
            // Both reasons are kept: the vocabulary unions...
            merged.RejectionReasons.ShouldBe(["Course is full", "Student already enrolled"], ignoreOrder: true);

            // ...and the one the model never declared is a finding, not a disagreement
            var hotspot = merged.Hotspots.ShouldHaveSingleItem();
            hotspot.Origin.ShouldBe(HotspotOrigin.UndeclaredRejection);
            hotspot.Role.ShouldBe(EventModelRole.RejectionReasons);
            hotspot.Text.ShouldBe("A specification refuses with 'Student already enrolled', which the slice does not declare. Declare it with Refuses(\"Student already enrolled\")");
        }
    }

    [Fact]
    public void a_model_that_declares_no_reasons_takes_the_specs_reasons_silently()
    {
        var declared = EventModelSliceDescriptor.Named("EnrolStudent") with { Provenance = EventModelProvenance.Declared };
        var specified = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            RejectionReasons = ["Student already enrolled"],
            Provenance = EventModelProvenance.Specified,
        };

        var merged = declared.Merge(specified);
        merged.RejectionReasons.ShouldBe(["Student already enrolled"]);
        merged.Hotspots.ShouldBeEmpty();
    }

    [Fact]
    public void the_undeclared_rejection_factory_carries_the_spec_when_the_caller_knows_it()
    {
        var hotspot = HotspotDescriptor.UndeclaredRejection("Course is full", "Enrol Student/Rejects a full course");

        hotspot.Origin.ShouldBe(HotspotOrigin.UndeclaredRejection);
        hotspot.SpecificationIdentity.ShouldBe("Enrol Student/Rejects a full course");
        hotspot.Text.ShouldContain("Refuses(\"Course is full\")");
    }

    [Fact]
    public void reads_are_replaced_by_a_higher_rung_and_the_dropped_read_is_recorded()
    {
        var declared = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            Reads = [new BoundaryReadDescriptor(BoundaryReadKind.Model, T<CourseEnrolment>(), "courseId", "CourseId")],
            Provenance = EventModelProvenance.Declared,
        };
        var derived = EventModelSliceDescriptor.Named("EnrolStudent") with
        {
            Reads = [new BoundaryReadDescriptor(BoundaryReadKind.Model, T<CourseEnrolment>(), "studentId", "StudentId")],
            Provenance = EventModelProvenance.Derived,
        };

        var merged = declared.Merge(derived);
        merged.Reads.ShouldHaveSingleItem().Tag.ShouldBe("studentId");
        var hotspot = merged.Hotspots.ShouldHaveSingleItem();
        hotspot.Role.ShouldBe(EventModelRole.Reads);
        hotspot.Text.ShouldBe("Reads: Derived claims CourseEnrolment by studentId; Declared claims CourseEnrolment by courseId");
    }

    // ---- wire ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void everything_round_trips_and_the_boundary_query_is_recomputed(bool critterWatchWire)
    {
        var options = critterWatchWire ? CritterWatchWire : new JsonSerializerOptions();
        var descriptor = build();

        var json = JsonSerializer.Serialize(descriptor, options);
        var back = JsonSerializer.Deserialize<EventModelDescriptor>(json, options)!;

        back.Tags.ShouldBe(descriptor.Tags);
        var slice = back.Slices.Single();
        slice.EventTags.ShouldBe(descriptor.Slices.Single().EventTags);
        slice.Reads.ShouldBe(descriptor.Slices.Single().Reads);
        slice.UntaggedProjections.ShouldBe([T<EnrolmentReport>()]);
        slice.RejectionReasons.ShouldBe(["Course is full", "Student already enrolled"]);
        slice.BoundaryQuery.ShouldBe(descriptor.Slices.Single().BoundaryQuery);

        // The query goes OUT, so a viewer draws it with no second transform
        json.ShouldContain(critterWatchWire ? "\"boundaryQuery\":{" : "\"BoundaryQuery\":{");
    }

    [Fact]
    public void a_payload_from_before_dcb_still_deserializes()
    {
        const string json = """
            {"name":"Enrolments","slices":[{"name":"EnrolStudent","emittedEvents":[],"projectionTypes":[],"readModelTypes":[]}]}
            """;

        var back = JsonSerializer.Deserialize<EventModelDescriptor>(json, CritterWatchWire)!;
        back.Tags.ShouldBeEmpty();
        back.Shapes.ShouldBeEmpty();
        var slice = back.Slices.Single();
        slice.EventTags.ShouldBeEmpty();
        slice.Reads.ShouldBeEmpty();
        slice.RejectionReasons.ShouldBeEmpty();
        slice.BoundaryQuery.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void the_new_roles_and_origins_are_appended_so_existing_wire_values_keep_their_meaning()
    {
        ((int)EventModelRole.TriggeredBy).ShouldBe(25);
        ((int)EventModelRole.EventTags).ShouldBe(26);
        ((int)EventModelRole.Reads).ShouldBe(27);
        ((int)EventModelRole.UntaggedProjections).ShouldBe(28);
        ((int)EventModelRole.RejectionReasons).ShouldBe(29);
        ((int)EventModelRole.TagType).ShouldBe(30);
        ((int)EventModelRole.Actor).ShouldBe(31);
        ((int)EventModelRole.ExternalEvents).ShouldBe(32);
        ((int)EventModelRole.Schedule).ShouldBe(33);
        ((int)EventModelRole.Screen).ShouldBe(34);
        ((int)EventModelRole.Description).ShouldBe(35);
        ((int)EventModelRole.Fields).ShouldBe(36);

        ((int)HotspotOrigin.PatternViolation).ShouldBe(6);
        ((int)HotspotOrigin.UndeclaredRejection).ShouldBe(7);
        ((int)HotspotOrigin.IncompleteInformation).ShouldBe(8);

        ((int)EventModelElementKind.Hotspot).ShouldBe(9);
        ((int)EventModelElementKind.Screen).ShouldBe(10);

        // Model-level roles: no slice ever claims them
        EventModelSliceDescriptor.Named("x").Claims(EventModelRole.TagType).ShouldBeFalse();
        EventModelSliceDescriptor.Named("x").Claims(EventModelRole.Fields).ShouldBeFalse();
    }
}
