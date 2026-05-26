// Smoke test verifying the assembly loads and the simplest piece of the
// public surface (VmaVirtualBlock) works end-to-end without a Vulkan device.

using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class SmokeTests
    {
        [Fact]
        public void Smoke_VmaVirtualBlock_CreateAllocateFreeDispose()
        {
            var info = new VmaVirtualBlockCreateInfo { Size = 65536 };
            Result r = VmaVirtualBlock.Create(in info, out var block);
            Assert.Equal(Result.Success, r);
            Assert.NotNull(block);

            var ci = new VmaVirtualAllocationCreateInfo { Size = 1024 };
            Result ar = block!.Allocate(in ci, out var alloc, out ulong offset);
            Assert.Equal(Result.Success, ar);
            Assert.False(alloc.IsNull);

            block.Free(alloc);
            Assert.True(block.IsEmpty());

            block.Dispose();
        }
    }
}
