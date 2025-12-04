using System.Text;
using Microsoft.Extensions.ObjectPool;

namespace JasperFx.CodeGeneration;

internal static class CodeGenerationObjectPool
{
    public static readonly ObjectPool<StringBuilder> StringBuilderPool = new DefaultObjectPoolProvider().CreateStringBuilderPool(
        initialCapacity: 4096,
        maximumRetainedCapacity: 16384
        );
}