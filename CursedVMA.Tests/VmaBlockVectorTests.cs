// Tests for VmaBlockVector lifecycle operations: CreateBlock, FreeEmptyBlocks,
// Destroy, and statistics accumulation.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaBlockVectorCreateBlockTests
    {
        private static readonly Device s_Device = new Device((nint)1);

        private static VmaBlockVector MakeVector(
            FakeVulkanFunctions vk,
            nuint maxBlockCount = default,
            nuint minBlockCount = 0,
            VmaPoolCreateFlags algorithm = VmaPoolCreateFlags.None)
        {
            nuint effectiveMax = maxBlockCount == 0 ? nuint.MaxValue : maxBlockCount;
            return new VmaBlockVector(
                vk, s_Device, allocationCallbacks: null,
                memoryTypeIndex: 0,
                preferredBlockSize: 65536,
                minBlockCount: minBlockCount,
                maxBlockCount: effectiveMax,
                bufferImageGranularity: 1,
                algorithm: algorithm,
                explicitBlockSize: false,
                minAllocationAlignment: 1);
        }

        [Fact]
        public void Init_ZeroMinBlockCount_CreatesNoBlocks()
        {
            var vk = new FakeVulkanFunctions();
            var vector = MakeVector(vk);

            Result r = vector.Init();

            Assert.Equal(Result.Success, r);
            Assert.Equal(0, vector.BlockCount);
            Assert.Equal(0, vk.AllocateMemoryCallCount);
        }

        [Fact]
        public void Init_WithMinBlockCount_AllocatesMinimumBlocks()
        {
            var vk = new FakeVulkanFunctions();
            var vector = MakeVector(vk, minBlockCount: 3);

            Result r = vector.Init();

            Assert.Equal(Result.Success, r);
            Assert.Equal(3, vector.BlockCount);
            Assert.Equal(3, vk.AllocateMemoryCallCount);
        }

        [Fact]
        public void CreateBlock_Success_IncreasesBlockCount()
        {
            var vk = new FakeVulkanFunctions();
            var vector = MakeVector(vk);
            vector.Init();

            Result r = vector.CreateBlock(65536, out int idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0, idx);
            Assert.Equal(1, vector.BlockCount);
            Assert.Equal(1, vk.AllocateMemoryCallCount);
        }

        [Fact]
        public void CreateBlock_MultipleBlocks_IndexesAreSequential()
        {
            var vk = new FakeVulkanFunctions();
            var vector = MakeVector(vk);
            vector.Init();

            vector.CreateBlock(65536, out int idx0);
            vector.CreateBlock(65536, out int idx1);

            Assert.Equal(0, idx0);
            Assert.Equal(1, idx1);
            Assert.Equal(2, vector.BlockCount);
        }

        [Fact]
        public void CreateBlock_AtMaxCapacity_ReturnsOutOfDeviceMemory()
        {
            var vk = new FakeVulkanFunctions();
            var vector = MakeVector(vk, maxBlockCount: 1);
            vector.Init();

            vector.CreateBlock(65536, out _);
            Result r = vector.CreateBlock(65536, out _);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Equal(1, vector.BlockCount);
        }

        [Fact]
        public void CreateBlock_AllocateFails_PropagatesError()
        {
            var vk = new FakeVulkanFunctions
            {
                AllocateMemoryResult = Result.ErrorOutOfDeviceMemory,
            };
            var vector = MakeVector(vk);
            vector.Init();

            Result r = vector.CreateBlock(65536, out _);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Equal(0, vector.BlockCount);
        }
    }

    public sealed class VmaBlockVectorLifecycleTests
    {
        private static readonly Device s_Device = new Device((nint)1);

        private static VmaBlockVector MakeVectorWithBlocks(
            FakeVulkanFunctions vk,
            int blockCount)
        {
            var vector = new VmaBlockVector(
                vk, s_Device, allocationCallbacks: null,
                memoryTypeIndex: 0,
                preferredBlockSize: 65536,
                minBlockCount: 0,
                maxBlockCount: nuint.MaxValue,
                bufferImageGranularity: 1,
                algorithm: VmaPoolCreateFlags.None,
                explicitBlockSize: false,
                minAllocationAlignment: 1);
            vector.Init();
            for (int i = 0; i < blockCount; i++)
                vector.CreateBlock(65536, out _);
            return vector;
        }

        [Fact]
        public void Destroy_FreesAllBlocks()
        {
            var vk = new FakeVulkanFunctions();
            var vector = MakeVectorWithBlocks(vk, 3);

            vector.Destroy();

            Assert.Equal(3, vk.FreeMemoryCallCount);
            Assert.Equal(0, vector.BlockCount);
        }

        [Fact]
        public void Destroy_EmptyVector_IsNoOp()
        {
            var vk = new FakeVulkanFunctions();
            var vector = MakeVectorWithBlocks(vk, 0);

            vector.Destroy();

            Assert.Equal(0, vk.FreeMemoryCallCount);
        }

        [Fact]
        public void FreeEmptyBlocks_RemovesAllEmptyBlocks()
        {
            var vk = new FakeVulkanFunctions();
            var vector = MakeVectorWithBlocks(vk, 2);
            int priorAllocs = vk.AllocateMemoryCallCount;

            vector.FreeEmptyBlocks();

            Assert.Equal(0, vector.BlockCount);
            Assert.Equal(2, vk.FreeMemoryCallCount);
        }

        [Fact]
        public void FreeEmptyBlocks_RespectsMinBlockCount()
        {
            var vk = new FakeVulkanFunctions();
            var vector = new VmaBlockVector(
                vk, s_Device, allocationCallbacks: null,
                memoryTypeIndex: 0,
                preferredBlockSize: 65536,
                minBlockCount: 1,
                maxBlockCount: nuint.MaxValue,
                bufferImageGranularity: 1,
                algorithm: VmaPoolCreateFlags.None,
                explicitBlockSize: false,
                minAllocationAlignment: 1);
            vector.Init(); // creates 1 min block
            vector.CreateBlock(65536, out _); // 2 blocks total

            vector.FreeEmptyBlocks(); // should keep 1

            Assert.Equal(1, vector.BlockCount);
            Assert.Equal(1, vk.FreeMemoryCallCount);
        }
    }

    public sealed class VmaBlockVectorStatisticsTests
    {
        private static readonly Device s_Device = new Device((nint)1);

        [Fact]
        public void AddStatistics_ReflectsCreatedBlocks()
        {
            var vk = new FakeVulkanFunctions();
            var vector = new VmaBlockVector(
                vk, s_Device, null,
                0, 65536, 0, nuint.MaxValue, 1,
                VmaPoolCreateFlags.None, false, 1);
            vector.Init();
            vector.CreateBlock(65536, out _);
            vector.CreateBlock(32768, out _);

            VmaStatistics stats = default;
            vector.AddStatistics(ref stats);

            Assert.Equal(2u, stats.BlockCount);
            Assert.Equal(65536ul + 32768ul, stats.BlockBytes);
            Assert.Equal(0u, stats.AllocationCount);
        }

        [Fact]
        public void AddStatistics_EmptyVector_AllZero()
        {
            var vk = new FakeVulkanFunctions();
            var vector = new VmaBlockVector(
                vk, s_Device, null,
                0, 65536, 0, nuint.MaxValue, 1,
                VmaPoolCreateFlags.None, false, 1);
            vector.Init();

            VmaStatistics stats = default;
            vector.AddStatistics(ref stats);

            Assert.Equal(0u, stats.BlockCount);
            Assert.Equal(0ul, stats.BlockBytes);
        }
    }
}
