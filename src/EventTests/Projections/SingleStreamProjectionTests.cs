using JasperFx.Events.Aggregation;

namespace EventTests.Projections;

public class SingleStreamProjectionTests
{
    
}

public class FakeSingleProjectionStream<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, FakeOperations, FakeSession>
{
    public FakeSingleProjectionStream(Type[] transientExceptionTypes) : base(transientExceptionTypes)
    {
    }
}

