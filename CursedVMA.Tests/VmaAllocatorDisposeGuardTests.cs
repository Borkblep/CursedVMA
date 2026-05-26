// Phase 24b tests: ObjectDisposedException propagation across every public
// VmaAllocator and VmaPool entry point that calls RequireNotDisposed. The
// goal is to catch any future refactor that drops a dispose guard.
//
// Each test creates a small allocator (and, where needed, an allocation),
// disposes it, then exercises the method and asserts the throw.

using Silk.NET.Vulkan;
using System;
using Xunit;

namespace CursedVMA.Tests
{
    internal static class DisposeGuardFixture
    {
        internal static (FakeVulkanFunctions, VmaAllocator) MakeAllocator(
            VmaAllocatorCreateFlags flags = VmaAllocatorCreateFlags.None)
        {
            var fvk = new FakeVulkanFunctions();
            fvk.MemoryProperties.MemoryHeapCount = 1;
            fvk.MemoryProperties.MemoryTypeCount = 1;
            unsafe
            {
                fvk.MemoryProperties.MemoryHeaps[0] =
                    new MemoryHeap { Size = 64ul * 1024 * 1024 };
                fvk.MemoryProperties.MemoryTypes[0] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                                  | MemoryPropertyFlags.HostCoherentBit
                                  | MemoryPropertyFlags.DeviceLocalBit,
                };
            }
            fvk.BufferMemoryRequirements = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            fvk.ImageMemoryRequirements = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
                Flags          = flags,
            };
            VmaAllocator.Create(fvk, info, out var a);
            return (fvk, a!);
        }

        // Allocates one live VmaAllocation from the allocator BEFORE disposing,
        // so dispose-guard tests for allocation-taking methods have an argument
        // to pass. The allocation reference outlives the allocator and is not
        // valid for any real operation — it's only used to reach the guard.
        internal static VmaAllocation LiveAllocation(VmaAllocator allocator)
        {
            var req = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            return alloc!;
        }
    }

    public sealed class VmaAllocatorDisposeGuardTests
    {
        [Fact]
        public void AfterDispose_AllocateMemory_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var req = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo();
            Assert.Throws<ObjectDisposedException>(
                () => a.AllocateMemory(in req, in ci, out _));
        }

        [Fact]
        public void AfterDispose_AllocateMemoryForBuffer_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var ci = new VmaAllocationCreateInfo();
            Assert.Throws<ObjectDisposedException>(
                () => a.AllocateMemoryForBuffer(default, in ci, out _));
        }

        [Fact]
        public void AfterDispose_AllocateMemoryForImage_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var ci = new VmaAllocationCreateInfo();
            Assert.Throws<ObjectDisposedException>(
                () => a.AllocateMemoryForImage(default, in ci, out _));
        }

        [Fact]
        public void AfterDispose_FreeMemory_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var alloc = DisposeGuardFixture.LiveAllocation(a);
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(() => a.FreeMemory(alloc));
        }

        [Fact]
        public void AfterDispose_MapMemory_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var alloc = DisposeGuardFixture.LiveAllocation(a);
            a.Dispose();
            unsafe
            {
                Assert.Throws<ObjectDisposedException>(() => a.MapMemory(alloc, out void* _));
            }
        }

        [Fact]
        public void AfterDispose_UnmapMemory_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var alloc = DisposeGuardFixture.LiveAllocation(a);
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(() => a.UnmapMemory(alloc));
        }

        [Fact]
        public void AfterDispose_BindBufferMemory_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var alloc = DisposeGuardFixture.LiveAllocation(a);
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => a.BindBufferMemory(alloc, default));
        }

        [Fact]
        public void AfterDispose_BindImageMemory_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var alloc = DisposeGuardFixture.LiveAllocation(a);
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => a.BindImageMemory(alloc, default));
        }

        [Fact]
        public void AfterDispose_CreatePool_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var ci = new VmaPoolCreateInfo { MemoryTypeIndex = 0 };
            Assert.Throws<ObjectDisposedException>(
                () => a.CreatePool(in ci, out _));
        }

        [Fact]
        public void AfterDispose_DestroyPool_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var poolCi = new VmaPoolCreateInfo { MemoryTypeIndex = 0 };
            a.CreatePool(in poolCi, out var pool);
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(() => a.DestroyPool(pool!));
        }

        [Fact]
        public void AfterDispose_FindMemoryTypeIndex_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var ci = new VmaAllocationCreateInfo();
            Assert.Throws<ObjectDisposedException>(
                () => a.FindMemoryTypeIndex(uint.MaxValue, in ci, out _));
        }

        [Fact]
        public void AfterDispose_FindMemoryTypeIndexForBufferInfo_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo };
            var aci = new VmaAllocationCreateInfo();
            Assert.Throws<ObjectDisposedException>(
                () => a.FindMemoryTypeIndexForBufferInfo(in bci, in aci, out _));
        }

        [Fact]
        public void AfterDispose_FindMemoryTypeIndexForImageInfo_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var ici = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            var aci = new VmaAllocationCreateInfo();
            Assert.Throws<ObjectDisposedException>(
                () => a.FindMemoryTypeIndexForImageInfo(in ici, in aci, out _));
        }

        [Fact]
        public void AfterDispose_GetHeapBudgets_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var buf = new VmaBudget[1];
            Assert.Throws<ObjectDisposedException>(() => a.GetHeapBudgets(buf));
        }

        [Fact]
        public void AfterDispose_GetStatistics_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var buf = new VmaStatistics[1];
            Assert.Throws<ObjectDisposedException>(() => a.GetStatistics(buf));
        }

        [Fact]
        public void AfterDispose_CalculateStatistics_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(() => a.CalculateStatistics(out _));
        }

        [Fact]
        public void AfterDispose_BeginDefragmentation_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var info = new VmaDefragmentationInfo();
            Assert.Throws<ObjectDisposedException>(
                () => a.BeginDefragmentation(in info, out _));
        }

        [Fact]
        public void AfterDispose_CheckCorruption_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(() => a.CheckCorruption(uint.MaxValue));
        }

        [Fact]
        public void AfterDispose_SetCurrentFrameIndex_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(() => a.SetCurrentFrameIndex(42));
        }

        [Fact]
        public void AfterDispose_SetAllocationName_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var alloc = DisposeGuardFixture.LiveAllocation(a);
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => a.SetAllocationName(alloc, "x"));
        }

        [Fact]
        public void AfterDispose_SetAllocationUserData_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var alloc = DisposeGuardFixture.LiveAllocation(a);
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => a.SetAllocationUserData(alloc, new object()));
        }

        [Fact]
        public void AfterDispose_CreateBuffer_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 64 };
            var aci = new VmaAllocationCreateInfo();
            Assert.Throws<ObjectDisposedException>(
                () => a.CreateBuffer(in bci, in aci, out _, out _, out _));
        }

        [Fact]
        public void AfterDispose_CreateImage_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            var ici = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            var aci = new VmaAllocationCreateInfo();
            Assert.Throws<ObjectDisposedException>(
                () => a.CreateImage(in ici, in aci, out _, out _, out _));
        }

        [Fact]
        public void AfterDispose_GetMemoryProperties_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(() => a.GetMemoryProperties(out _));
        }

        [Fact]
        public void AfterDispose_GetAllocationInfo_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var alloc = DisposeGuardFixture.LiveAllocation(a);
            a.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => a.GetAllocationInfo(alloc, out _));
        }
    }

    public sealed class VmaPoolDisposeGuardTests
    {
        [Fact]
        public void AfterDispose_GetStatistics_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var poolCi = new VmaPoolCreateInfo { MemoryTypeIndex = 0 };
            a.CreatePool(in poolCi, out var pool);
            pool!.Dispose();
            Assert.Throws<ObjectDisposedException>(() => pool.GetStatistics(out _));
            a.Dispose();
        }

        [Fact]
        public void AfterDispose_CalculateStatistics_Throws()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var poolCi = new VmaPoolCreateInfo { MemoryTypeIndex = 0 };
            a.CreatePool(in poolCi, out var pool);
            pool!.Dispose();
            Assert.Throws<ObjectDisposedException>(() => pool.CalculateStatistics(out _));
            a.Dispose();
        }

        [Fact]
        public void AfterDispose_DisposeAgain_IsSafe()
        {
            var (_, a) = DisposeGuardFixture.MakeAllocator();
            var poolCi = new VmaPoolCreateInfo { MemoryTypeIndex = 0 };
            a.CreatePool(in poolCi, out var pool);
            pool!.Dispose();
            pool.Dispose(); // second Dispose must not throw
            a.Dispose();
        }
    }
}
