// Tests for VmaAllocator lifecycle: Create and Dispose. All GPU calls go
// through FakeVulkanFunctions so no real device is required.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaAllocatorCreateTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device s_Device = new Device((nint)1);

        private static FakeVulkanFunctions MakeFakeVk(uint memoryTypeCount = 2)
        {
            var vk = new FakeVulkanFunctions();

            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = memoryTypeCount;
            if (memoryTypeCount >= 1)
            {
                memProps.MemoryTypes.Element0 = new MemoryType
                {
                    PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                    HeapIndex = 0,
                };
            }
            if (memoryTypeCount >= 2)
            {
                memProps.MemoryTypes.Element1 = new MemoryType
                {
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                                  | MemoryPropertyFlags.HostCoherentBit,
                    HeapIndex = 1,
                };
            }
            memProps.MemoryHeapCount = 2;
            memProps.MemoryHeaps.Element0 = new MemoryHeap
            {
                Size = 8ul * 1024 * 1024 * 1024,
                Flags = MemoryHeapFlags.DeviceLocalBit,
            };
            memProps.MemoryHeaps.Element1 = new MemoryHeap
            {
                Size = 16ul * 1024 * 1024 * 1024,
            };

            vk.MemoryProperties = memProps;
            return vk;
        }

        private static VmaAllocatorCreateInfo MakeCreateInfo(FakeVulkanFunctions vk = null!)
            => new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device = s_Device,
            };

        [Fact]
        public void Create_NullPhysicalDevice_ReturnsInitializationFailed()
        {
            var vk = MakeFakeVk();
            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = default,
                Device = s_Device,
            };

            Result r = VmaAllocator.Create(vk, in info, out var allocator);

            Assert.Equal(Result.ErrorInitializationFailed, r);
            Assert.Null(allocator);
        }

        [Fact]
        public void Create_NullDevice_ReturnsInitializationFailed()
        {
            var vk = MakeFakeVk();
            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device = default,
            };

            Result r = VmaAllocator.Create(vk, in info, out var allocator);

            Assert.Equal(Result.ErrorInitializationFailed, r);
            Assert.Null(allocator);
        }

        [Fact]
        public void Create_NullVulkanApi_ReturnsInitializationFailed()
        {
            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device = s_Device,
                VulkanApi = null,
            };

            Result r = VmaAllocator.Create(in info, out var allocator);

            Assert.Equal(Result.ErrorInitializationFailed, r);
            Assert.Null(allocator);
        }

        [Fact]
        public void Create_ValidParams_ReturnsSuccess()
        {
            var vk = MakeFakeVk();
            var info = MakeCreateInfo();

            Result r = VmaAllocator.Create(vk, in info, out var allocator);

            Assert.Equal(Result.Success, r);
            Assert.NotNull(allocator);
            allocator!.Dispose();
        }

        [Fact]
        public void Create_QueriesPhysicalDeviceMemoryProperties()
        {
            var vk = MakeFakeVk();
            var info = MakeCreateInfo();

            VmaAllocator.Create(vk, in info, out var allocator);

            Assert.Equal(2u, allocator!.MemoryTypeCount);
            allocator.Dispose();
        }

        [Fact]
        public void Create_ZeroMemoryTypes_Succeeds()
        {
            var vk = MakeFakeVk(memoryTypeCount: 0);
            var info = MakeCreateInfo();

            Result r = VmaAllocator.Create(vk, in info, out var allocator);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, allocator!.MemoryTypeCount);
            allocator.Dispose();
        }

        [Fact]
        public void Create_PreferredBlockSizeZero_UsesDefault256MB()
        {
            var vk = MakeFakeVk(memoryTypeCount: 1);
            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device = s_Device,
                PreferredLargeHeapBlockSize = 0,
            };

            VmaAllocator.Create(vk, in info, out var allocator);

            // Heap[0] = 8 GB > 1 GB, so block size = AlignUp(256 MB, 32) = 256 MB
            var bv = allocator!.GetDefaultBlockVector(0)!;
            Assert.Equal(256ul * 1024 * 1024, bv.PreferredBlockSize);
            allocator.Dispose();
        }

        [Fact]
        public void Create_SmallHeap_UsesEighthOfHeapSize()
        {
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 1;
            memProps.MemoryTypes.Element0 = new MemoryType { HeapIndex = 0 };
            memProps.MemoryHeapCount = 1;
            // 512 MB heap — smaller than SmallHeapMaxSize (1 GB)
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = 512ul * 1024 * 1024 };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device = s_Device,
            };
            VmaAllocator.Create(vk, in info, out var allocator);

            // 512 MB / 8 = 64 MB; AlignUp(64 MB, 32) = 64 MB
            var bv = allocator!.GetDefaultBlockVector(0)!;
            Assert.Equal(64ul * 1024 * 1024, bv.PreferredBlockSize);
            allocator.Dispose();
        }

        [Fact]
        public void GetMemoryType_ReturnsCorrectFlags()
        {
            var vk = MakeFakeVk();
            var info = MakeCreateInfo();
            VmaAllocator.Create(vk, in info, out var allocator);

            var mt0 = allocator!.GetMemoryType(0);
            var mt1 = allocator.GetMemoryType(1);

            Assert.True((mt0.PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0);
            Assert.True((mt1.PropertyFlags & MemoryPropertyFlags.HostVisibleBit) != 0);
            allocator.Dispose();
        }

        [Fact]
        public void GetDefaultBlockVector_ReturnsVectorPerMemoryType()
        {
            var vk = MakeFakeVk();
            var info = MakeCreateInfo();
            VmaAllocator.Create(vk, in info, out var allocator);

            var bv0 = allocator!.GetDefaultBlockVector(0);
            var bv1 = allocator.GetDefaultBlockVector(1);

            Assert.NotNull(bv0);
            Assert.NotNull(bv1);
            Assert.NotSame(bv0, bv1);
            Assert.Equal(0u, bv0!.MemoryTypeIndex);
            Assert.Equal(1u, bv1!.MemoryTypeIndex);
            allocator.Dispose();
        }
    }

    public sealed class VmaAllocatorDisposeTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device s_Device = new Device((nint)1);

        private static (FakeVulkanFunctions, VmaAllocator) MakeAllocator(uint memTypeCount = 2)
        {
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = memTypeCount;
            for (int i = 0; i < (int)memTypeCount; i++)
                memProps.MemoryHeapCount = System.Math.Max(memProps.MemoryHeapCount, 1);
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

        [Fact]
        public void Dispose_WithNoBlocks_DoesNotCallFreeMemory()
        {
            var (vk, allocator) = MakeAllocator();

            allocator.Dispose();

            Assert.Equal(0, vk.FreeMemoryCallCount);
        }

        [Fact]
        public void Dispose_Twice_IsSafe()
        {
            var (_, allocator) = MakeAllocator();

            allocator.Dispose();
            allocator.Dispose(); // no throw
        }

        [Fact]
        public void Dispose_NullsOutBlockVectors()
        {
            var (_, allocator) = MakeAllocator(memTypeCount: 2);

            allocator.Dispose();

            Assert.Null(allocator.GetDefaultBlockVector(0));
            Assert.Null(allocator.GetDefaultBlockVector(1));
        }
    }
}
