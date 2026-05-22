// Tests for Phase 10 dedicated allocations: VmaAllocationCreateFlags.DedicatedMemoryBit
// causes VmaAllocator.AllocateMemory to allocate its own VkDeviceMemory rather
// than carving a suballocation out of a shared block.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaAllocatorDedicatedAllocateTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device s_Device = new Device((nint)1);

        private static (FakeVulkanFunctions, VmaAllocator) MakeAllocator()
        {
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 2;
            memProps.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                HeapIndex = 0,
            };
            memProps.MemoryTypes.Element1 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.HostCoherentBit,
                HeapIndex = 1,
            };
            memProps.MemoryHeapCount = 2;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = 8ul * 1024 * 1024 * 1024 };
            memProps.MemoryHeaps.Element1 = new MemoryHeap { Size = 4ul * 1024 * 1024 * 1024 };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device = s_Device,
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            return (vk, allocator!);
        }

        private static MemoryRequirements MakeReq(ulong size = 1024, uint memTypeBits = 0b11u)
            => new MemoryRequirements { Size = size, Alignment = 1, MemoryTypeBits = memTypeBits };

        [Fact]
        public void AllocateMemory_DedicatedMemoryBit_AllocatesOwnDeviceMemory()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = MakeReq(size: 4096);
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            Result r = allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Equal(Result.Success, r);
            Assert.NotNull(alloc);
            Assert.Equal(1, vk.AllocateMemoryCallCount); // exactly one vkAllocateMemory
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void AllocateMemory_DedicatedAllocation_OffsetIsZero()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = MakeReq();
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Equal(0ul, alloc!.Offset);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void AllocateMemory_DedicatedAllocation_DoesNotCreateBlockVectorBlock()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = MakeReq();
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            // Default block vectors should be empty — dedicated path bypasses them.
            Assert.Equal(0, allocator.GetDefaultBlockVector(0)!.BlockCount);
            Assert.Equal(0, allocator.GetDefaultBlockVector(1)!.BlockCount);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void AllocateMemory_DedicatedAllocation_TracksInDedicatedList()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            Assert.Equal(0, allocator.DedicatedAllocationCount);

            var req = MakeReq();
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Equal(1, allocator.DedicatedAllocationCount);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void AllocateMemory_DedicatedAllocateFails_PropagatesError()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            vk.AllocateMemoryResult = Result.ErrorOutOfDeviceMemory;
            var req = MakeReq();
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            Result r = allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Null(alloc);
            Assert.Equal(0, allocator.DedicatedAllocationCount);
        }

        [Fact]
        public void AllocateMemory_DedicatedAllocation_HasNonZeroMemoryHandle()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = MakeReq();
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.NotEqual(0ul, alloc!.Memory.Handle);
            allocator.FreeMemory(alloc);
        }
    }

    public sealed class VmaAllocatorDedicatedFreeTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device s_Device = new Device((nint)1);

        private static (FakeVulkanFunctions, VmaAllocator) MakeAllocator()
        {
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 1;
            memProps.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                HeapIndex = 0,
            };
            memProps.MemoryHeapCount = 1;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = 4ul * 1024 * 1024 * 1024 };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device = s_Device,
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            return (vk, allocator!);
        }

        private static VmaAllocation DedicatedAlloc(VmaAllocator allocator, ulong size = 1024)
        {
            var req = new MemoryRequirements { Size = size, Alignment = 1, MemoryTypeBits = 1u };
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            return alloc!;
        }

        [Fact]
        public void FreeMemory_DedicatedAllocation_CallsVkFreeMemory()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = DedicatedAlloc(allocator);
            int freesBefore = vk.FreeMemoryCallCount;

            allocator.FreeMemory(alloc);

            Assert.Equal(freesBefore + 1, vk.FreeMemoryCallCount);
        }

        [Fact]
        public void FreeMemory_DedicatedAllocation_RemovesFromTrackingList()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = DedicatedAlloc(allocator);
            Assert.Equal(1, allocator.DedicatedAllocationCount);

            allocator.FreeMemory(alloc);

            Assert.Equal(0, allocator.DedicatedAllocationCount);
        }

        [Fact]
        public void Dispose_FreesRemainingDedicatedAllocations()
        {
            var (vk, allocator) = MakeAllocator();

            // Leak two dedicated allocations.
            DedicatedAlloc(allocator);
            DedicatedAlloc(allocator);
            int freesBefore = vk.FreeMemoryCallCount;

            allocator.Dispose();

            Assert.Equal(freesBefore + 2, vk.FreeMemoryCallCount);
        }
    }

    public sealed class VmaDedicatedAllocationMapBindTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device s_Device = new Device((nint)1);

        private static (FakeVulkanFunctions, VmaAllocator) MakeAllocator()
        {
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 1;
            memProps.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.HostCoherentBit,
                HeapIndex = 0,
            };
            memProps.MemoryHeapCount = 1;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = 4ul * 1024 * 1024 * 1024 };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device = s_Device,
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            return (vk, allocator!);
        }

        private static VmaAllocation DedicatedAlloc(VmaAllocator allocator)
        {
            var req = new MemoryRequirements { Size = 4096, Alignment = 1, MemoryTypeBits = 1u };
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.CpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            return alloc!;
        }

        [Fact]
        public unsafe void MapMemory_DedicatedAllocation_CallsVkMapMemory()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = DedicatedAlloc(allocator);
            Result r = allocator.MapMemory(alloc, out void* p);

            Assert.Equal(Result.Success, r);
            Assert.True(p != null);
            Assert.Equal(1, vk.MapMemoryCallCount);
            allocator.UnmapMemory(alloc);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public unsafe void MapMemory_DedicatedAllocation_IsReferenceCounted()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = DedicatedAlloc(allocator);
            allocator.MapMemory(alloc, out void* p0);
            allocator.MapMemory(alloc, out void* p1);

            // Second map should not trigger vkMapMemory again.
            Assert.Equal(1, vk.MapMemoryCallCount);
            Assert.True(p0 == p1);

            allocator.UnmapMemory(alloc);
            Assert.Equal(0, vk.UnmapMemoryCallCount); // still mapped
            allocator.UnmapMemory(alloc);
            Assert.Equal(1, vk.UnmapMemoryCallCount); // last unmap
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void BindBufferMemory_DedicatedAllocation_PassesOwnDeviceMemory()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = DedicatedAlloc(allocator);
            var buffer = new Silk.NET.Vulkan.Buffer(1ul);
            Result r = allocator.BindBufferMemory(alloc, buffer);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, vk.BindBufferMemoryCallCount);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void BindImageMemory_DedicatedAllocation_PassesOwnDeviceMemory()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = DedicatedAlloc(allocator);
            var image = new Image(1ul);
            Result r = allocator.BindImageMemory(alloc, image);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, vk.BindImageMemoryCallCount);
            allocator.FreeMemory(alloc);
        }
    }

    public sealed class VmaAllocationInfo2Tests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device s_Device = new Device((nint)1);

        private static (FakeVulkanFunctions, VmaAllocator) MakeAllocator()
        {
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 1;
            memProps.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                HeapIndex = 0,
            };
            memProps.MemoryHeapCount = 1;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = 8ul * 1024 * 1024 * 1024 };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device = s_Device,
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            return (vk, allocator!);
        }

        [Fact]
        public void GetAllocationInfo2_BlockAllocation_ReportsBlockSizeAndNotDedicated()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1u };
            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            allocator.GetAllocationInfo2(alloc!, out var info);

            Assert.False(info.DedicatedMemory);
            // BlockSize should be the underlying VkDeviceMemory block size (256 MB default).
            Assert.Equal(256ul * 1024 * 1024, info.BlockSize);
            Assert.Equal(1024ul, info.AllocationInfo.Size);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void GetAllocationInfo2_DedicatedAllocation_ReportsAllocSizeAndDedicated()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements { Size = 4096, Alignment = 1, MemoryTypeBits = 1u };
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            allocator.GetAllocationInfo2(alloc!, out var info);

            Assert.True(info.DedicatedMemory);
            Assert.Equal(4096ul, info.BlockSize); // equals alloc size
            Assert.Equal(4096ul, info.AllocationInfo.Size);
            allocator.FreeMemory(alloc);
        }
    }
}
