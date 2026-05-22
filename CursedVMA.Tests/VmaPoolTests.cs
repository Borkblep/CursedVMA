// Tests for VmaPool and VmaAllocator.CreatePool / DestroyPool. All GPU calls
// go through FakeVulkanFunctions so no real device is required.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaAllocatorCreatePoolTests
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
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
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
        public void CreatePool_InvalidMemoryTypeIndex_ReturnsInitializationFailed()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var createInfo = new VmaPoolCreateInfo { MemoryTypeIndex = 99 };
            Result r = allocator.CreatePool(in createInfo, out var pool);

            Assert.Equal(Result.ErrorInitializationFailed, r);
            Assert.Null(pool);
        }

        [Fact]
        public void CreatePool_ValidParams_ReturnsSuccess()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var createInfo = new VmaPoolCreateInfo { MemoryTypeIndex = 0, BlockSize = 65536 };
            Result r = allocator.CreatePool(in createInfo, out var pool);

            Assert.Equal(Result.Success, r);
            Assert.NotNull(pool);
            allocator.DestroyPool(pool!);
        }

        [Fact]
        public void CreatePool_ExplicitBlockSize_IsRespected()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            const ulong explicitSize = 1_048_576ul;
            var createInfo = new VmaPoolCreateInfo { MemoryTypeIndex = 0, BlockSize = explicitSize };
            allocator.CreatePool(in createInfo, out var pool);

            Assert.Equal(explicitSize, pool!.BlockVector.PreferredBlockSize);
            allocator.DestroyPool(pool);
        }

        [Fact]
        public void CreatePool_ZeroBlockSize_DerivedFromHeap()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            // Heap[0] = 8 GB > 1 GB → AlignUp(256 MB, 32) = 256 MB
            var createInfo = new VmaPoolCreateInfo { MemoryTypeIndex = 0, BlockSize = 0 };
            allocator.CreatePool(in createInfo, out var pool);

            Assert.Equal(256ul * 1024 * 1024, pool!.BlockVector.PreferredBlockSize);
            allocator.DestroyPool(pool);
        }

        [Fact]
        public void CreatePool_LinearAlgorithmFlag_Succeeds()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var createInfo = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                BlockSize = 65536,
                Flags = VmaPoolCreateFlags.LinearAlgorithmBit,
            };
            Result r = allocator.CreatePool(in createInfo, out var pool);

            Assert.Equal(Result.Success, r);
            Assert.NotNull(pool);
            allocator.DestroyPool(pool!);
        }

        [Fact]
        public void CreatePool_MinBlockCount_PreallocatesBlocks()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            int allocsBefore = vk.AllocateMemoryCallCount;
            var createInfo = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                BlockSize = 65536,
                MinBlockCount = 3,
            };
            allocator.CreatePool(in createInfo, out var pool);

            Assert.Equal(allocsBefore + 3, vk.AllocateMemoryCallCount);
            Assert.Equal(3, pool!.BlockVector.BlockCount);
            allocator.DestroyPool(pool);
        }

        [Fact]
        public void CreatePool_MaxBlockCountZero_AllowsUnlimitedBlocks()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var createInfo = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                BlockSize = 65536,
                MaxBlockCount = 0,
            };
            allocator.CreatePool(in createInfo, out var pool);

            for (int i = 0; i < 5; i++)
            {
                Result cr = pool!.BlockVector.CreateBlock(65536, out _);
                Assert.Equal(Result.Success, cr);
            }
            allocator.DestroyPool(pool!);
        }

        [Fact]
        public void CreatePool_IncrementsPoolCount()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            Assert.Equal(0, allocator.PoolCount);

            var createInfo = new VmaPoolCreateInfo { MemoryTypeIndex = 0, BlockSize = 65536 };
            allocator.CreatePool(in createInfo, out var pool);

            Assert.Equal(1, allocator.PoolCount);
            allocator.DestroyPool(pool!);
        }
    }

    public sealed class VmaAllocatorDestroyPoolTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device s_Device = new Device((nint)1);

        private static (FakeVulkanFunctions, VmaAllocator) MakeAllocator()
        {
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 1;
            memProps.MemoryTypes.Element0 = new MemoryType { HeapIndex = 0 };
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

        private static VmaPool MakePool(VmaAllocator allocator, nuint minBlockCount = 0)
        {
            var createInfo = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                BlockSize = 65536,
                MinBlockCount = minBlockCount,
            };
            allocator.CreatePool(in createInfo, out var pool);
            return pool!;
        }

        [Fact]
        public void DestroyPool_DecrementsPoolCount()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var pool = MakePool(allocator);
            Assert.Equal(1, allocator.PoolCount);

            allocator.DestroyPool(pool);

            Assert.Equal(0, allocator.PoolCount);
        }

        [Fact]
        public void DestroyPool_FreesPreallocatedBlocks()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var pool = MakePool(allocator, minBlockCount: 2);
            int freesBefore = vk.FreeMemoryCallCount;

            allocator.DestroyPool(pool);

            Assert.Equal(freesBefore + 2, vk.FreeMemoryCallCount);
        }

        [Fact]
        public void DestroyPool_MultiplePoolsAreIndependent()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var pool0 = MakePool(allocator, minBlockCount: 1);
            var pool1 = MakePool(allocator, minBlockCount: 2);
            Assert.Equal(2, allocator.PoolCount);

            allocator.DestroyPool(pool0);
            Assert.Equal(1, allocator.PoolCount);
            Assert.Equal(1, vk.FreeMemoryCallCount);

            allocator.DestroyPool(pool1);
            Assert.Equal(0, allocator.PoolCount);
            Assert.Equal(3, vk.FreeMemoryCallCount);
        }
    }

    public sealed class VmaPoolPropertiesTests
    {
        private static readonly Device s_Device = new Device((nint)1);

        private static VmaPool MakePool(FakeVulkanFunctions vk, nuint minBlockCount = 0, ulong blockSize = 65536)
        {
            var blockVector = new VmaBlockVector(
                vk, s_Device, allocationCallbacks: null,
                memoryTypeIndex: 0,
                preferredBlockSize: blockSize,
                minBlockCount: minBlockCount,
                maxBlockCount: nuint.MaxValue,
                bufferImageGranularity: 1,
                algorithm: VmaPoolCreateFlags.None,
                explicitBlockSize: blockSize != 0,
                minAllocationAlignment: 1);
            blockVector.Init();
            return new VmaPool(blockVector, id: 0);
        }

        [Fact]
        public void Name_InitiallyNull()
        {
            var vk = new FakeVulkanFunctions();
            using var pool = MakePool(vk);

            Assert.Null(pool.Name);
        }

        [Fact]
        public void SetName_RoundTrip()
        {
            var vk = new FakeVulkanFunctions();
            using var pool = MakePool(vk);

            pool.SetName("TestPool");

            Assert.Equal("TestPool", pool.Name);
        }

        [Fact]
        public void SetName_ClearWithNull()
        {
            var vk = new FakeVulkanFunctions();
            using var pool = MakePool(vk);

            pool.SetName("Initial");
            pool.SetName(null);

            Assert.Null(pool.Name);
        }

        [Fact]
        public void GetStatistics_EmptyPool_AllZero()
        {
            var vk = new FakeVulkanFunctions();
            using var pool = MakePool(vk);

            pool.GetStatistics(out var stats);

            Assert.Equal(0u, stats.BlockCount);
            Assert.Equal(0u, stats.AllocationCount);
            Assert.Equal(0ul, stats.BlockBytes);
            Assert.Equal(0ul, stats.AllocationBytes);
        }

        [Fact]
        public void GetStatistics_WithPreallocatedBlocks_ReflectsBlockCountAndBytes()
        {
            var vk = new FakeVulkanFunctions();
            using var pool = MakePool(vk, minBlockCount: 2, blockSize: 65536);

            pool.GetStatistics(out var stats);

            Assert.Equal(2u, stats.BlockCount);
            Assert.Equal(2ul * 65536ul, stats.BlockBytes);
            Assert.Equal(0u, stats.AllocationCount);
        }

        [Fact]
        public void CalculateStatistics_EmptyPool_AllZero()
        {
            var vk = new FakeVulkanFunctions();
            using var pool = MakePool(vk);

            pool.CalculateStatistics(out var stats);

            Assert.Equal(0u, stats.Statistics.BlockCount);
            Assert.Equal(0ul, stats.Statistics.BlockBytes);
        }

        [Fact]
        public void CalculateStatistics_WithBlocks_ReflectsBlockCount()
        {
            var vk = new FakeVulkanFunctions();
            using var pool = MakePool(vk, minBlockCount: 3, blockSize: 65536);

            pool.CalculateStatistics(out var stats);

            Assert.Equal(3u, stats.Statistics.BlockCount);
            Assert.Equal(3ul * 65536ul, stats.Statistics.BlockBytes);
        }

        [Fact]
        public void Dispose_FreesVkDeviceMemory()
        {
            var vk = new FakeVulkanFunctions();
            var pool = MakePool(vk, minBlockCount: 2);
            int freesBefore = vk.FreeMemoryCallCount;

            pool.Dispose();

            Assert.Equal(freesBefore + 2, vk.FreeMemoryCallCount);
        }

        [Fact]
        public void Dispose_Twice_IsSafe()
        {
            var vk = new FakeVulkanFunctions();
            var pool = MakePool(vk, minBlockCount: 1);

            pool.Dispose();
            pool.Dispose(); // no throw
        }
    }
}
