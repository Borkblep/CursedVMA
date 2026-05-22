// Tests for VmaDeviceMemoryBlock. All Vulkan dispatch is provided by
// FakeVulkanFunctions, which avoids any dependency on real GPU hardware.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaDeviceMemoryBlockCreateTests
    {
        private static readonly Device s_Device = new Device((nint)1);

        private static unsafe VmaDeviceMemoryBlock MakeBlock(
            FakeVulkanFunctions vk,
            ulong size = 65536,
            VmaPoolCreateFlags algorithm = VmaPoolCreateFlags.None)
        {
            Result r = VmaDeviceMemoryBlock.Create(
                vk, s_Device, null,
                memoryTypeIndex: 0,
                size: size,
                id: 0,
                algorithm: algorithm,
                bufferImageGranularity: 1,
                out VmaDeviceMemoryBlock? block);
            Assert.Equal(Result.Success, r);
            return block!;
        }

        [Fact]
        public unsafe void Create_Success_AllocatesMemory()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);

            Assert.Equal(1, vk.AllocateMemoryCallCount);
            Assert.NotEqual(0ul, block.Memory.Handle);
        }

        [Fact]
        public unsafe void Create_Success_FieldsMatchArguments()
        {
            var vk = new FakeVulkanFunctions();
            Result r = VmaDeviceMemoryBlock.Create(
                vk, s_Device, null,
                memoryTypeIndex: 3,
                size: 4096,
                id: 7,
                algorithm: VmaPoolCreateFlags.None,
                bufferImageGranularity: 64,
                out var block);

            Assert.Equal(Result.Success, r);
            Assert.Equal(3u, block!.MemoryTypeIndex);
            Assert.Equal(7u, block.Id);
            Assert.Equal(4096ul, block.Metadata.GetSize());
        }

        [Fact]
        public unsafe void Create_ZeroSize_ReturnsInitializationFailed()
        {
            var vk = new FakeVulkanFunctions();
            Result r = VmaDeviceMemoryBlock.Create(
                vk, s_Device, null, 0, size: 0, 0, 0, 1, out var block);

            Assert.Equal(Result.ErrorInitializationFailed, r);
            Assert.Null(block);
            Assert.Equal(0, vk.AllocateMemoryCallCount);
        }

        [Fact]
        public unsafe void Create_AllocateMemoryFails_PropagatesError()
        {
            var vk = new FakeVulkanFunctions
            {
                AllocateMemoryResult = Result.ErrorOutOfDeviceMemory,
            };
            Result r = VmaDeviceMemoryBlock.Create(
                vk, s_Device, null, 0, 65536, 0, 0, 1, out var block);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Null(block);
        }

        [Fact]
        public unsafe void Create_NewBlock_IsEmpty()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);
            Assert.True(block.IsEmpty());
        }

        [Fact]
        public unsafe void Create_WithLinearAlgorithm_Succeeds()
        {
            var vk = new FakeVulkanFunctions();
            Result r = VmaDeviceMemoryBlock.Create(
                vk, s_Device, null, 0, 65536, 0,
                VmaPoolCreateFlags.LinearAlgorithmBit, 1, out var block);

            Assert.Equal(Result.Success, r);
            Assert.True(block!.IsEmpty());
        }
    }

    public sealed class VmaDeviceMemoryBlockMapTests
    {
        private static readonly Device s_Device = new Device((nint)1);

        private static unsafe VmaDeviceMemoryBlock MakeBlock(FakeVulkanFunctions vk)
        {
            VmaDeviceMemoryBlock.Create(
                vk, s_Device, null, 0, 65536, 0, 0, 1, out var block);
            return block!;
        }

        [Fact]
        public unsafe void Map_ZeroCount_DoesNotCallVkMapMemory()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);

            Result r = block.Map(vk, s_Device, 0, out void* ppData);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0, vk.MapMemoryCallCount);
            Assert.Equal(0, block.MapCount);
        }

        [Fact]
        public unsafe void Map_InitialMap_CallsVkMapMemoryOnce()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);

            Result r = block.Map(vk, s_Device, 1, out void* ppData);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, vk.MapMemoryCallCount);
            Assert.Equal(1, block.MapCount);
            Assert.True(ppData != null);
        }

        [Fact]
        public unsafe void Map_SubsequentMap_DoesNotCallVkMapMemoryAgain()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);

            block.Map(vk, s_Device, 1, out void* first);
            block.Map(vk, s_Device, 1, out void* second);

            Assert.Equal(1, vk.MapMemoryCallCount);
            Assert.Equal(2, block.MapCount);
        }

        [Fact]
        public unsafe void Map_SubsequentMap_ReturnsSamePointer()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);

            block.Map(vk, s_Device, 1, out void* first);
            block.Map(vk, s_Device, 1, out void* second);

            Assert.Equal((nint)first, (nint)second);
        }

        [Fact]
        public unsafe void Map_MappedData_NonNullAfterMap()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);

            block.Map(vk, s_Device, 1, out _);

            Assert.True(block.MappedData != null);
        }

        [Fact]
        public unsafe void Unmap_ToZero_CallsVkUnmapMemory()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);

            block.Map(vk, s_Device, 1, out _);
            block.Unmap(vk, s_Device, 1);

            Assert.Equal(1, vk.UnmapMemoryCallCount);
            Assert.Equal(0, block.MapCount);
        }

        [Fact]
        public unsafe void Unmap_RefcountNotZero_DoesNotCallVkUnmapMemory()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);

            block.Map(vk, s_Device, 2, out _);
            block.Unmap(vk, s_Device, 1);

            Assert.Equal(0, vk.UnmapMemoryCallCount);
            Assert.Equal(1, block.MapCount);
        }

        [Fact]
        public unsafe void Unmap_ZeroCount_IsNoOp()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);

            block.Map(vk, s_Device, 1, out _);
            block.Unmap(vk, s_Device, 0);

            Assert.Equal(0, vk.UnmapMemoryCallCount);
            Assert.Equal(1, block.MapCount);
        }

        [Fact]
        public unsafe void Unmap_MappedDataClearedAfterFinalUnmap()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);

            block.Map(vk, s_Device, 1, out _);
            block.Unmap(vk, s_Device, 1);

            Assert.True(block.MappedData == null);
        }
    }

    public sealed class VmaDeviceMemoryBlockBindTests
    {
        private static readonly Device s_Device = new Device((nint)1);

        private static unsafe VmaDeviceMemoryBlock MakeBlock(FakeVulkanFunctions vk)
        {
            VmaDeviceMemoryBlock.Create(
                vk, s_Device, null, 0, 65536, 0, 0, 1, out var block);
            return block!;
        }

        [Fact]
        public unsafe void BindBufferMemory_NullPNext_CallsVkBindBufferMemory()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);
            var buffer = new Silk.NET.Vulkan.Buffer(42ul);

            Result r = block.BindBufferMemory(vk, s_Device, 0, buffer, null);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, vk.BindBufferMemoryCallCount);
            Assert.Equal(0, vk.BindBufferMemory2CallCount);
        }

        [Fact]
        public unsafe void BindBufferMemory_WithPNext_CallsVkBindBufferMemory2()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);
            var buffer = new Silk.NET.Vulkan.Buffer(42ul);
            int sentinel = 99;

            Result r = block.BindBufferMemory(vk, s_Device, 0, buffer, &sentinel);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0, vk.BindBufferMemoryCallCount);
            Assert.Equal(1, vk.BindBufferMemory2CallCount);
        }

        [Fact]
        public unsafe void BindImageMemory_NullPNext_CallsVkBindImageMemory()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);
            var image = new Image(77ul);

            Result r = block.BindImageMemory(vk, s_Device, 0, image, null);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1, vk.BindImageMemoryCallCount);
            Assert.Equal(0, vk.BindImageMemory2CallCount);
        }

        [Fact]
        public unsafe void BindImageMemory_WithPNext_CallsVkBindImageMemory2()
        {
            var vk = new FakeVulkanFunctions();
            var block = MakeBlock(vk);
            var image = new Image(77ul);
            int sentinel = 99;

            Result r = block.BindImageMemory(vk, s_Device, 0, image, &sentinel);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0, vk.BindImageMemoryCallCount);
            Assert.Equal(1, vk.BindImageMemory2CallCount);
        }
    }

    public sealed class VmaDeviceMemoryBlockDestroyTests
    {
        private static readonly Device s_Device = new Device((nint)1);

        [Fact]
        public unsafe void Destroy_CallsFreeMemory()
        {
            var vk = new FakeVulkanFunctions();
            VmaDeviceMemoryBlock.Create(
                vk, s_Device, null, 0, 65536, 0, 0, 1, out var block);

            block!.Destroy(vk, s_Device, null);

            Assert.Equal(1, vk.FreeMemoryCallCount);
        }

        [Fact]
        public unsafe void Destroy_MemoryHandleZeroedAfterFree()
        {
            var vk = new FakeVulkanFunctions();
            VmaDeviceMemoryBlock.Create(
                vk, s_Device, null, 0, 65536, 0, 0, 1, out var block);

            block!.Destroy(vk, s_Device, null);

            Assert.Equal(0ul, block.Memory.Handle);
        }
    }
}
