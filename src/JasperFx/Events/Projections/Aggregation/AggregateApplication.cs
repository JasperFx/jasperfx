using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.CodeGeneration;

namespace JasperFx.Events.Projections.Aggregation;

// TODO -- add CancellationToken to this? Ick, but yes.
// TODO -- set version as well?
// TODO -- apply metadata
// TODO -- introduce string constants
public partial class AggregateApplication<TAggregate, TQuerySession>
{
    public static readonly string ApplyMethod = "Apply";
    public static readonly string ShouldDeleteMethod = "ShouldDelete";
    public static readonly string CreateMethod = "Create";
    
    // This would be for external projections
    private readonly object _projection;
    private readonly Type _projectionType;
    
    public AggregateApplication()
    {
        _projection = null;
        _projectionType = null;
    }

    public AggregateApplication(object projection)
    {
        _projection = projection;
        _projectionType = projection.GetType();
    }
    
    // internal override IEnumerable<string> ValidateConfiguration(StoreOptions options)
    // {
    //     var mapping = options.Storage.FindMapping(typeof(T)).Root.As<DocumentMapping>();
    //
    //     foreach (var p in validateDocumentIdentity(options, mapping)) yield return p;
    //
    //     if (options.Events.TenancyStyle != mapping.TenancyStyle
    //         && (options.Events.TenancyStyle == TenancyStyle.Single
    //             || options.Events is
    //                 { TenancyStyle: TenancyStyle.Conjoined, EnableGlobalProjectionsForConjoinedTenancy: false })
    //        )
    //     {
    //         yield return
    //             $"Tenancy storage style mismatch between the events ({options.Events.TenancyStyle}) and the aggregate type {typeof(T).FullNameInCode()} ({mapping.TenancyStyle})";
    //     }
    //
    //     if (mapping.DeleteStyle == DeleteStyle.SoftDelete)
    //     {
    //         yield return
    //             "AggregateProjection cannot support aggregates that are soft-deleted";
    //     }
    // }
    

}