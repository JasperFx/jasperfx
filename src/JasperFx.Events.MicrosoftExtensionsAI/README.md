# JasperFx.Events.MicrosoftExtensionsAI

Adapts a [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)
`IEmbeddingGenerator<string, Embedding<float>>` to the Critter Stack's store-neutral
`IEmbeddingProvider`, so one embedding model configured for OpenAI, Azure OpenAI, Ollama, ONNX or any
other M.E.AI provider drives vector search in Marten, Polecat and Fisher.

```csharp
using JasperFx.Events.MicrosoftExtensionsAI;

IEmbeddingGenerator<string, Embedding<float>> generator = /* from your M.E.AI provider package */;

// Dimensions come from the generator's metadata when it publishes them...
IEmbeddingProvider provider = generator.AsEmbeddingProvider();

// ...or say them explicitly, which always wins.
IEmbeddingProvider provider = generator.AsEmbeddingProvider(dimensions: 1536);
```

The adapter forwards batches and enforces the provider contract before a store sees the result:
one vector per input, in input order, every vector of the declared length. It takes no vendor SDK,
only `Microsoft.Extensions.AI.Abstractions`.
