// Phase 24c tests: VmaPoolCreateInfo.MemoryAllocateNext threading and
// TypeExternalMemoryHandleTypes array shorter than memory type count.
//
// MemoryAllocateNext stores an opaque pNext chain pointer that VMA must
// inject into every VkMemoryAllocateInfo it builds for that pool. We feed
// a known MemoryDedicatedAllocateInfo (its SType is recognizable) and
// confirm it appears in FakeVulkanFunctions.LastAllocatePNextChainTypes.

using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed unsafe class VmaPoolMemoryAllocateNextTests
    {
        private static (FakeVulkanFunctions, VmaAllocator) MakeAllocator(
            VmaAllocatorCreateFlags flags = VmaAllocatorCreateFlags.None)
        {
            var fvk = new FakeVulkanFunctions();
            fvk.MemoryProperties.MemoryHeapCount = 1;
            fvk.MemoryProperties.MemoryTypeCount = 1;
            fvk.MemoryProperties.MemoryHeaps[0] =
                new MemoryHeap { Size = 64ul * 1024 * 1024 };
            fvk.MemoryProperties.MemoryTypes[0] = new MemoryType
            {
                HeapIndex     = 0,
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
            };
            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
                Flags          = flags,
            };
            VmaAllocator.Create(fvk, ci, out var a);
            return (fvk, a!);
        }

        [Fact]
        public void MemoryAllocateNext_ChainedIntoVkAllocateMemory()
        {
            var (fvk, allocator) = MakeAllocator();

            // Build a MemoryDedicatedAllocateInfo on the stack; we only need
            // its SType to appear in the recorded chain. The struct is kept
            // alive for the duration of the test scope.
            var ded = new MemoryDedicatedAllocateInfo
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                PNext = null,
            };

            var poolCi = new VmaPoolCreateInfo
            {
                MemoryTypeIndex    = 0,
                MemoryAllocateNext = &ded,
            };
            Result pr = allocator.CreatePool(in poolCi, out var pool);
            Assert.Equal(Result.Success, pr);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var aci = new VmaAllocationCreateInfo { Pool = pool };

            Result r = allocator.AllocateMemory(in req, in aci, out var alloc);
            Assert.Equal(Result.Success, r);
            Assert.Contains(StructureType.MemoryDedicatedAllocateInfo,
                fvk.LastAllocatePNextChainTypes);

            allocator.FreeMemory(alloc);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }

        [Fact]
        public void MemoryAllocateNext_ChainedAlongsidePriority()
        {
            var (fvk, allocator) = MakeAllocator(
                VmaAllocatorCreateFlags.ExtMemoryPriorityBit);

            var ded = new MemoryDedicatedAllocateInfo
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                PNext = null,
            };
            var poolCi = new VmaPoolCreateInfo
            {
                MemoryTypeIndex    = 0,
                MemoryAllocateNext = &ded,
                Priority           = 0.7f,
            };
            allocator.CreatePool(in poolCi, out var pool);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var aci = new VmaAllocationCreateInfo { Pool = pool };
            allocator.AllocateMemory(in req, in aci, out var alloc);

            // Both should be in the chain in some order.
            Assert.Contains(StructureType.MemoryDedicatedAllocateInfo,
                fvk.LastAllocatePNextChainTypes);
            Assert.Contains(StructureType.MemoryPriorityAllocateInfoExt,
                fvk.LastAllocatePNextChainTypes);

            allocator.FreeMemory(alloc);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }

        [Fact]
        public void NullMemoryAllocateNext_NoExtraStructChained()
        {
            var (fvk, allocator) = MakeAllocator();

            var poolCi = new VmaPoolCreateInfo
            {
                MemoryTypeIndex    = 0,
                MemoryAllocateNext = null,
            };
            allocator.CreatePool(in poolCi, out var pool);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var aci = new VmaAllocationCreateInfo { Pool = pool };
            allocator.AllocateMemory(in req, in aci, out var alloc);

            Assert.Empty(fvk.LastAllocatePNextChainTypes);

            allocator.FreeMemory(alloc);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }
    }

    public sealed class VmaTypeExternalHandleArrayLengthTests
    {
        private static FakeVulkanFunctions MakeFvk()
        {
            var fvk = new FakeVulkanFunctions();
            fvk.MemoryProperties.MemoryHeapCount = 1;
            fvk.MemoryProperties.MemoryTypeCount = 4;
            unsafe
            {
                fvk.MemoryProperties.MemoryHeaps[0] =
                    new MemoryHeap { Size = 64ul * 1024 * 1024 };
                for (int i = 0; i < 4; i++)
                {
                    fvk.MemoryProperties.MemoryTypes[i] = new MemoryType
                    {
                        HeapIndex     = 0,
                        PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                    };
                }
            }
            return fvk;
        }

        [Fact]
        public void ShorterThanTypeCount_AllocationFromTypeWithinArrayLength_ChainsExportInfo()
        {
            var fvk = MakeFvk();
            // Array length 2; only type 0 has a non-zero handle type.
            var handles = new[]
            {
                ExternalMemoryHandleTypeFlags.OpaqueFDBit,
                (ExternalMemoryHandleTypeFlags)0,
            };
            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice                = new PhysicalDevice(1),
                Device                        = new Device(1),
                TypeExternalMemoryHandleTypes = handles,
            };
            VmaAllocator.Create(fvk, ci, out var allocator);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 0b0001 };
            var aci = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };
            allocator!.AllocateMemory(in req, in aci, out var alloc);

            Assert.Equal(0u, fvk.LastAllocateMemoryTypeIndex);
            Assert.Equal(ExternalMemoryHandleTypeFlags.OpaqueFDBit,
                fvk.LastAllocateExportHandleTypes);

            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }

        [Fact]
        public void ShorterThanTypeCount_AllocationFromTypeBeyondArrayLength_NoExportInfo()
        {
            var fvk = MakeFvk();
            // Array length 2, but we allocate from type 3 (beyond the array).
            var handles = new[]
            {
                ExternalMemoryHandleTypeFlags.OpaqueFDBit,
                ExternalMemoryHandleTypeFlags.OpaqueFDBit,
            };
            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice                = new PhysicalDevice(1),
                Device                        = new Device(1),
                TypeExternalMemoryHandleTypes = handles,
            };
            VmaAllocator.Create(fvk, ci, out var allocator);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 0b1000 };
            var aci = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };
            Result r = allocator!.AllocateMemory(in req, in aci, out var alloc);

            Assert.Equal(Result.Success, r);
            Assert.Equal(3u, fvk.LastAllocateMemoryTypeIndex);
            Assert.Equal((ExternalMemoryHandleTypeFlags)0,
                fvk.LastAllocateExportHandleTypes);

            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }
    }
}
