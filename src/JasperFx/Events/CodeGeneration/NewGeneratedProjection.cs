using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.CodeGeneration;


public interface IStore<TOptions>
{
    TOptions Options { get; }
    GenerationRules GenerationRules { get; }
    IEventGraph Events { get; }
}

public class GeneratedEventProjection<TOperations, TStore, TDatabase, TOptions> : NewGeneratedProjection<TOperations, TStore,
    TDatabase, TOptions>
    where TStore : IStore<TOptions>
{
    private readonly ProjectMethodCollection _projectMethods;
    private readonly CreateDocumentMethodCollection _createMethods;

    public GeneratedEventProjection(Type projectionType) : base(projectionType)
    {
        _projectMethods = new ProjectMethodCollection(projectionType, typeof(TOperations));
        _createMethods = new CreateDocumentMethodCollection(projectionType, typeof(TOperations));
    }

    protected override void assembleTypes(GeneratedAssembly assembly, TOptions options)
    {
        throw new NotImplementedException();
    }

    protected override bool tryAttachTypes(Assembly assembly, TOptions options)
    {
        throw new NotImplementedException();
    }

    protected override bool needsSettersGenerated()
    {
        throw new NotImplementedException();
    }
}

public abstract class NewGeneratedProjection<TOperations, TStore, TDatabase, TOptions> : ICodeFile
    where TStore : IStore<TOptions>
{
    private readonly Type _projectionType;
    private bool _hasGenerated;

    public NewGeneratedProjection(Type projectionType)
    {
        _projectionType = projectionType;
    }
    
    internal TOptions StoreOptions { get; set; }
    
    bool ICodeFile.AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider services,
        string containingNamespace)
    {
        return tryAttachTypes(assembly, StoreOptions);
    }

    public string FileName => GetType().ToSuffixedTypeName("RuntimeSupport");
    
    
    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        if (_hasGenerated)
            return;

        lock (_assembleLocker)
        {
            if (_hasGenerated)
                return;
            assembleTypes(assembly, StoreOptions);
            _hasGenerated = true;
        }
    }

    Task<bool> ICodeFile.AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider services,
        string containingNamespace)
    {
        var attached = tryAttachTypes(assembly, StoreOptions);
        return Task.FromResult(attached);
    }
    
    public Type ProjectionType => GetType();

    protected abstract void assembleTypes(GeneratedAssembly assembly, TOptions options);
    protected abstract bool tryAttachTypes(Assembly assembly, TOptions options);

    private void generateIfNecessary(TStore store)
    {
        lock (_assembleLocker)
        {
            if (_hasGenerated)
            {
                return;
            }

            generateIfNecessaryLocked();

            _hasGenerated = true;
        }

        return;

        void generateIfNecessaryLocked()
        {
            StoreOptions = store.Options;
            var rules = store.GenerationRules;
            rules.ReferenceTypes(GetType());
            this.As<ICodeFile>().InitializeSynchronously(rules, store.Events, null);

            if (!needsSettersGenerated())
            {
                return;
            }

            var generatedAssembly = new GeneratedAssembly(rules);
            assembleTypes(generatedAssembly, store.Options);

            // This will force it to create any setters or dynamic funcs
            generatedAssembly.GenerateCode();
        }
    }

    /// <summary>
    /// Prevent code generation bugs when multiple aggregates are code generated in parallel
    /// Happens more often on dynamic code generation
    /// </summary>
    protected static object _assembleLocker = new();
    
    protected abstract bool needsSettersGenerated();


}
