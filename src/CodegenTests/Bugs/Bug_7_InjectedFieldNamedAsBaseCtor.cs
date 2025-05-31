using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Shouldly;
using Xunit;

namespace CodegenTests.Bugs;

public class Bug_7_InjectedFieldNamedAsBaseCtor
{
    [Fact]
    public void ArrangeFrames_WhereInjectedFieldHasTheSameNameAsBaseClass_ShouldUseThatNameInBaseCall()
    {
        // Arrange
        var generatedAssembly = GeneratedAssembly.Empty();
        generatedAssembly.ReferenceAssembly(typeof(BaseClass).Assembly);
        var generatedType = generatedAssembly.AddType("ChildClass", typeof(BaseClass));
        generatedType.AllInjectedFields.Add(new InjectedField(typeof(string), "dependency"));
        
        // Act
        generatedType.ArrangeFrames();
        
        // Assert
        generatedType.AllInjectedFields[0].CtorArgDeclaration().ShouldBe("int __dependency1");
        generatedType.AllInjectedFields[1].CtorArgDeclaration().ShouldBe("string __dependency2");
       
        // This fails because the ctor argument was not renamed
        generatedType.BaseConstructorArguments[0].Usage.ShouldBe("__dependency1");
    }

    public class BaseClass
    {
        private readonly int dependency;

        public BaseClass(int dependency)
        {
            this.dependency = dependency;
        }
    }
}