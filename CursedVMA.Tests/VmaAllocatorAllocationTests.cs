// Tests for VmaAllocator Phase 9 entry-points: AllocateMemory, FreeMemory,
// MapMemory, UnmapMemory, GetAllocationInfo, SetAllocation*, BindBufferMemory,
// BindImageMemory, FindMemoryTypeIndex, and AllocateMemoryForBuffer/Image.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaAllocatorFindMemoryTypeIndexTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device s_Device = new Device((nint)1);

        // Build an allocator with two memory types:
        //   Type 0 → DEVICE_LOCAL on heap 0 (8 GB GPU heap)
        //   Type 1 → HOST_VISIBLE | HOST_COHERENT on heap 1 (4 GB system heap)
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

        [Fact]
        public void FindMemoryTypeIndex_GpuOnly_PrefersDeviceLocal()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var createInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            Result r = allocator.FindMemoryTypeIndex(0b11u, in createInfo, out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, idx); // type 0 = DEVICE_LOCAL
        }

        [Fact]
        public void FindMemoryTypeIndex_CpuOnly_RequiresHostVisible()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var createInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.CpuOnly };
            Result r = allocator.FindMemoryTypeIndex(0b11u, in createInfo, out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1u, idx); // type 1 = HOST_VISIBLE | HOST_COHERENT
        }

        [Fact]
        public void FindMemoryTypeIndex_MemoryTypeBitsFilter_LimitsSelection()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            // Only type 1 allowed.
            var createInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Unknown };
            Result r = allocator.FindMemoryTypeIndex(0b10u, in createInfo, out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1u, idx);
        }

        [Fact]
        public void FindMemoryTypeIndex_RequiredFlagsNotSatisfied_ReturnsNotPresent()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var createInfo = new VmaAllocationCreateInfo
            {
                RequiredFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.DeviceLocalBit, // no type has both
            };
            Result r = allocator.FindMemoryTypeIndex(0b11u, in createInfo, out _);

            Assert.Equal(Result.ErrorFeatureNotPresent, r);
        }

        [Fact]
        public void FindMemoryTypeIndex_MemoryTypeBitsZero_AllowsAll()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var createInfo = new VmaAllocationCreateInfo
            {
                MemoryTypeBits = 0,
                Usage = VmaMemoryUsage.CpuOnly,
            };
            Result r = allocator.FindMemoryTypeIndex(0b11u, in createInfo, out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1u, idx);
        }

        [Fact]
        public void FindMemoryTypeIndex_ExplicitRequiredFlags_OverridesUsageHint()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            // Usage=GpuOnly but required HOST_VISIBLE forces type 1.
            var createInfo = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                RequiredFlags = MemoryPropertyFlags.HostVisibleBit,
            };
            Result r = allocator.FindMemoryTypeIndex(0b11u, in createInfo, out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1u, idx);
        }
    }

    public sealed class VmaAllocatorAllocateMemoryTests
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

        private static MemoryRequirements MakeReq(
            ulong size = 1024,
            ulong alignment = 1,
            uint memTypeBits = 0b11u)
            => new MemoryRequirements
            {
                Size = size,
                Alignment = alignment,
                MemoryTypeBits = memTypeBits,
            };

        [Fact]
        public void AllocateMemory_BasicSucceeds()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = MakeReq();
            var createInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            Result r = allocator.AllocateMemory(in req, in createInfo, out var alloc);

            Assert.Equal(Result.Success, r);
            Assert.NotNull(alloc);
            Assert.Equal(1024ul, alloc!.Size);
            Assert.Equal(1, vk.AllocateMemoryCallCount); // one block allocated
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void AllocateMemory_SetsCorrectMemoryTypeIndex()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = MakeReq();
            var createInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in createInfo, out var alloc);

            Assert.Equal(0u, alloc!.MemoryTypeIndex); // type 0 = DEVICE_LOCAL
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void AllocateMemory_MemoryHandleIsNonZero()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = MakeReq();
            var createInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in createInfo, out var alloc);

            Assert.NotEqual(0ul, alloc!.Memory.Handle);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void AllocateMemory_OffsetIsAligned()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            const ulong alignment = 256;
            var req = MakeReq(alignment: alignment);
            var createInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in createInfo, out var alloc);

            Assert.Equal(0ul, alloc!.Offset % alignment);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void AllocateMemory_TwoAllocations_DifferentOffsets()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = MakeReq(size: 256);
            var createInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in createInfo, out var a0);
            allocator.AllocateMemory(in req, in createInfo, out var a1);

            // Both live in the same VkDeviceMemory block but at different offsets.
            Assert.Equal(a0!.Memory.Handle, a1!.Memory.Handle);
            Assert.NotEqual(a0.Offset, a1.Offset);
            allocator.FreeMemory(a0);
            allocator.FreeMemory(a1);
        }

        [Fact]
        public void AllocateMemory_WithPool_UsesPoolMemoryType()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            // Pool pinned to type 1 (HOST_VISIBLE).
            var poolInfo = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 1,
                BlockSize = 65536,
            };
            allocator.CreatePool(in poolInfo, out var pool);

            var req = MakeReq();
            var createInfo = new VmaAllocationCreateInfo { Pool = pool };
            Result r = allocator.AllocateMemory(in req, in createInfo, out var alloc);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1u, alloc!.MemoryTypeIndex);
            allocator.FreeMemory(alloc);
            allocator.DestroyPool(pool!);
        }

        [Fact]
        public void AllocateMemory_NeverAllocateBit_FailsWhenNoBlock()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            // NeverAllocate prevents creating a new block; default vectors start empty.
            var req = MakeReq();
            var createInfo = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.NeverAllocateBit,
            };
            Result r = allocator.AllocateMemory(in req, in createInfo, out var alloc);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Null(alloc);
        }

        [Fact]
        public void AllocateMemory_NoMatchingMemoryType_ReturnsFeatureNotPresent()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            // MemoryTypeBits = 0 → treated as all-allowed; but RequiredFlags is
            // unsatisfiable (both DEVICE_LOCAL and HOST_VISIBLE are required —
            // no type in this fixture has both).
            var req = MakeReq(memTypeBits: 0b11u);
            var createInfo = new VmaAllocationCreateInfo
            {
                RequiredFlags = MemoryPropertyFlags.DeviceLocalBit
                              | MemoryPropertyFlags.HostVisibleBit,
            };
            Result r = allocator.AllocateMemory(in req, in createInfo, out var alloc);

            Assert.Equal(Result.ErrorFeatureNotPresent, r);
            Assert.Null(alloc);
        }

        [Fact]
        public void AllocateMemory_WithUserData_StoresUserData()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            object userData = new object();
            var req = MakeReq();
            var createInfo = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                UserData = userData,
            };
            allocator.AllocateMemory(in req, in createInfo, out var alloc);

            Assert.Same(userData, alloc!.UserData);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void FreeMemory_ReleasesBlock_WhenLastAllocationFreed()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = MakeReq();
            var createInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in createInfo, out var alloc);

            int freesBefore = vk.FreeMemoryCallCount;
            allocator.FreeMemory(alloc);

            Assert.Equal(freesBefore + 1, vk.FreeMemoryCallCount);
        }

        [Fact]
        public void FreeMemory_NullAllocation_IsNoOp()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            allocator.FreeMemory(null); // must not throw

            Assert.Equal(0, vk.FreeMemoryCallCount);
        }
    }

    public sealed class VmaAllocatorAllocationInfoTests
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

        private static VmaAllocation Alloc(VmaAllocator allocator, ulong size = 1024)
        {
            var req = new MemoryRequirements { Size = size, Alignment = 1, MemoryTypeBits = 1u };
            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            return alloc!;
        }

        [Fact]
        public void GetAllocationInfo_ReturnsCorrectSizeAndOffset()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator, 2048);
            allocator.GetAllocationInfo(alloc, out var info);

            Assert.Equal(2048ul, info.Size);
            Assert.Equal(alloc.Offset, info.Offset);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void SetAllocationUserData_IsReflectedInGetInfo()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator);
            var tag = new object();
            allocator.SetAllocationUserData(alloc, tag);
            allocator.GetAllocationInfo(alloc, out var info);

            Assert.Same(tag, info.UserData);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void SetAllocationName_IsReflectedInGetInfo()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator);
            allocator.SetAllocationName(alloc, "MyBuffer");
            allocator.GetAllocationInfo(alloc, out var info);

            Assert.Equal("MyBuffer", info.Name);
            allocator.FreeMemory(alloc);
        }
    }

    public sealed class VmaAllocatorMapMemoryTests
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

        private static VmaAllocation Alloc(VmaAllocator allocator, ulong size = 1024)
        {
            var req = new MemoryRequirements { Size = size, Alignment = 1, MemoryTypeBits = 1u };
            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.CpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            return alloc!;
        }

        [Fact]
        public unsafe void MapMemory_ReturnsNonNullPointer()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator);
            Result r = allocator.MapMemory(alloc, out void* p);

            Assert.Equal(Result.Success, r);
            Assert.True(p != null);
            allocator.UnmapMemory(alloc);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public unsafe void MapMemory_InvokesVkMapMemory()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator);
            allocator.MapMemory(alloc, out _);

            Assert.Equal(1, vk.MapMemoryCallCount);
            allocator.UnmapMemory(alloc);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public unsafe void UnmapMemory_InvokesVkUnmapMemoryWhenRefCountDropsToZero()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator);
            allocator.MapMemory(alloc, out _);
            allocator.UnmapMemory(alloc);

            Assert.Equal(1, vk.UnmapMemoryCallCount);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public unsafe void MapMemory_MappedBitFlag_MapsOnAllocation()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1u };
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.CpuOnly,
                Flags = VmaAllocationCreateFlags.MappedBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            // The block should already be mapped.
            Assert.Equal(1, vk.MapMemoryCallCount);
            Assert.True(alloc!.MapCount > 0);

            allocator.GetAllocationInfo(alloc, out var info);
            Assert.True(info.MappedData != null);

            allocator.UnmapMemory(alloc); // undo the MappedBit map
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public unsafe void MapMemory_SecondCallSharesPointer()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator);
            allocator.MapMemory(alloc, out void* p0);
            allocator.MapMemory(alloc, out void* p1);

            // Second map should not call vkMapMemory again (refcount).
            Assert.Equal(1, vk.MapMemoryCallCount);
            Assert.True(p0 == p1);

            allocator.UnmapMemory(alloc);
            allocator.UnmapMemory(alloc);
            allocator.FreeMemory(alloc);
        }
    }

    public sealed class VmaAllocatorBindMemoryTests
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

        private static VmaAllocation Alloc(VmaAllocator allocator)
        {
            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 1u };
            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            return alloc!;
        }

        [Fact]
        public void BindBufferMemory_CallsVkBindBufferMemory()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator);
            var buffer = new Silk.NET.Vulkan.Buffer(1ul);
            Result r = allocator.BindBufferMemory(alloc, buffer);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, vk.BindBufferMemoryCallCount);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public unsafe void BindBufferMemory2_WithPNext_CallsVkBindBufferMemory2()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator);
            var buffer = new Silk.NET.Vulkan.Buffer(1ul);
            // Non-null pNext routes to vkBindBufferMemory2.
            byte dummy = 0;
            Result r = allocator.BindBufferMemory2(alloc, 0, buffer, &dummy);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, vk.BindBufferMemory2CallCount);
            Assert.Equal(0, vk.BindBufferMemoryCallCount);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void BindImageMemory_CallsVkBindImageMemory()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator);
            var image = new Image(1ul);
            Result r = allocator.BindImageMemory(alloc, image);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, vk.BindImageMemoryCallCount);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public unsafe void BindImageMemory2_WithPNext_CallsVkBindImageMemory2()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var alloc = Alloc(allocator);
            var image = new Image(1ul);
            byte dummy = 0;
            Result r = allocator.BindImageMemory2(alloc, 0, image, &dummy);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, vk.BindImageMemory2CallCount);
            Assert.Equal(0, vk.BindImageMemoryCallCount);
            allocator.FreeMemory(alloc);
        }
    }

    public sealed class VmaAllocatorForBufferImageTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device s_Device = new Device((nint)1);

        private static (FakeVulkanFunctions, VmaAllocator) MakeAllocator()
        {
            var vk = new FakeVulkanFunctions();
            // Both memory types available.
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 1;
            memProps.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                HeapIndex = 0,
            };
            memProps.MemoryHeapCount = 1;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = 8ul * 1024 * 1024 * 1024 };
            // FakeVulkanFunctions returns default MemoryRequirements (size=0, alignment=0,
            // memTypeBits=0). Override memTypeBits for meaningful type selection.
            vk.MemoryProperties = memProps;
            // Make GetBufferMemoryRequirements / GetImageMemoryRequirements return
            // something usable by pre-configuring fake results.
            vk.BufferMemoryRequirements = new MemoryRequirements
            {
                Size = 4096,
                Alignment = 4,
                MemoryTypeBits = 1u,
            };
            vk.ImageMemoryRequirements = new MemoryRequirements
            {
                Size = 8192,
                Alignment = 256,
                MemoryTypeBits = 1u,
            };

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device = s_Device,
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            return (vk, allocator!);
        }

        [Fact]
        public void AllocateMemoryForBuffer_UsesBufferRequirements()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var buffer = new Silk.NET.Vulkan.Buffer(1ul);
            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            Result r = allocator.AllocateMemoryForBuffer(buffer, in ci, out var alloc);

            Assert.Equal(Result.Success, r);
            Assert.Equal(4096ul, alloc!.Size);
            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void AllocateMemoryForImage_UsesImageRequirements()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var image = new Image(1ul);
            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            Result r = allocator.AllocateMemoryForImage(image, in ci, out var alloc);

            Assert.Equal(Result.Success, r);
            Assert.Equal(8192ul, alloc!.Size);
            allocator.FreeMemory(alloc);
        }
    }
}
