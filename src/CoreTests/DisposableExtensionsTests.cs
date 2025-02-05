using JasperFx.Core;
using NSubstitute;
using Shouldly;

namespace CoreTests
{
    public class DisposableExtensionsTests
    {
        [Fact]
        public async Task maybe_dispose_all()
        {
            var disposable = Substitute.For<IDisposable>();
            var asyncDisposable1 = Substitute.For<IAsyncDisposable>();
            var asyncDisposable2 = Substitute.For<IAsyncDisposable>();
            var objects = new List<object>
            {
                disposable,
                asyncDisposable1,
                asyncDisposable2,
                new object(),
                new object()
            };

            await objects.MaybeDisposeAllAsync();
            
            disposable.Received().Dispose();
            await asyncDisposable1.Received().DisposeAsync();
            await asyncDisposable2.Received().DisposeAsync();
        }
        
        [Fact]
        public void swallow_exceptions()
        {
            var blowsUpDisposing = new BlowsUpDisposing();
            blowsUpDisposing.SafeDispose();

            blowsUpDisposing.DisposeWasCalled.ShouldBeTrue();
        }

        public class BlowsUpDisposing : IDisposable
        {
            public void Dispose()
            {
                DisposeWasCalled = true;
                throw new Exception("You stink!");
            }

            public bool DisposeWasCalled { get; set; }
        }
    }


}