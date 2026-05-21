// Tests for the public VmaVirtualBlock API. Exercises both algorithm choices
// (TLSF by default, Linear via the LinearAlgorithmBit flag) and the error paths
// for invalid create-info / allocation parameters.

using Silk.NET.Vulkan;
using System;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaVirtualBlockCreateTests
    {
        [Fact]
        public void Create_ZeroSize_ReturnsInitializationFailed()
        {
            var info = new VmaVirtualBlockCreateInfo { Size = 0 };
            Result r = VmaVirtualBlock.Create(in info, out var block);
            Assert.Equal(Result.ErrorInitializationFailed, r);
            Assert.Null(block);
        }

        [Fact]
        public void Create_DefaultsToTlsf()
        {
            var info = new VmaVirtualBlockCreateInfo { Size = 65536 };
            Result r = VmaVirtualBlock.Create(in info, out var block);
            Assert.Equal(Result.Success, r);
            Assert.NotNull(block);
            Assert.True(block!.IsEmpty());
            block.Dispose();
        }

        [Fact]
        public void Create_WithLinearAlgorithm_Succeeds()
        {
            var info = new VmaVirtualBlockCreateInfo
            {
                Size = 65536,
                Flags = VmaVirtualBlockCreateFlags.LinearAlgorithmBit,
            };
            Result r = VmaVirtualBlock.Create(in info, out var block);
            Assert.Equal(Result.Success, r);
            Assert.NotNull(block);
            block!.Dispose();
        }

        [Fact]
        public void Dispose_WithLiveAllocation_DoesNotThrow()
        {
            // Mirrors C++ behavior: Dispose tears down the block even when
            // allocations are still live (the C++ port asserts in debug only).
            var info = new VmaVirtualBlockCreateInfo { Size = 4096 };
            VmaVirtualBlock.Create(in info, out var block);
            block!.Allocate(
                new VmaVirtualAllocationCreateInfo { Size = 256 },
                out _, out _);
            block.Dispose(); // no throw
        }

        [Fact]
        public void Dispose_Twice_IsSafe()
        {
            var info = new VmaVirtualBlockCreateInfo { Size = 4096 };
            VmaVirtualBlock.Create(in info, out var block);
            block!.Dispose();
            block.Dispose(); // no throw
        }
    }

    public sealed class VmaVirtualBlockAllocationTests
    {
        private static VmaVirtualBlock MakeBlock(ulong size, VmaVirtualBlockCreateFlags flags = 0)
        {
            var info = new VmaVirtualBlockCreateInfo { Size = size, Flags = flags };
            VmaVirtualBlock.Create(in info, out var block);
            return block!;
        }

        [Fact]
        public void Allocate_ZeroSize_ReturnsInitializationFailed()
        {
            using var block = MakeBlock(4096);
            Result r = block.Allocate(
                new VmaVirtualAllocationCreateInfo { Size = 0 },
                out var alloc, out var offset);
            Assert.Equal(Result.ErrorInitializationFailed, r);
            Assert.True(alloc.IsNull);
            Assert.Equal(0ul, offset);
        }

        [Fact]
        public void Allocate_NonPowerOfTwoAlignment_ReturnsInitializationFailed()
        {
            using var block = MakeBlock(4096);
            Result r = block.Allocate(
                new VmaVirtualAllocationCreateInfo { Size = 256, Alignment = 7 },
                out _, out _);
            Assert.Equal(Result.ErrorInitializationFailed, r);
        }

        [Fact]
        public void Allocate_ZeroAlignment_TreatedAsOne()
        {
            using var block = MakeBlock(4096);
            Result r = block.Allocate(
                new VmaVirtualAllocationCreateInfo { Size = 256, Alignment = 0 },
                out var alloc, out var offset);
            Assert.Equal(Result.Success, r);
            Assert.False(alloc.IsNull);
            Assert.Equal(0ul, offset);
        }

        [Fact]
        public void Allocate_TooLarge_ReturnsOutOfDeviceMemory()
        {
            using var block = MakeBlock(1024);
            Result r = block.Allocate(
                new VmaVirtualAllocationCreateInfo { Size = 2048 },
                out var alloc, out _);
            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.True(alloc.IsNull);
        }

        [Fact]
        public void Allocate_BasicSucceeds()
        {
            using var block = MakeBlock(65536);
            Result r = block.Allocate(
                new VmaVirtualAllocationCreateInfo { Size = 1024, UserData = "tag" },
                out var alloc, out var offset);
            Assert.Equal(Result.Success, r);
            Assert.False(alloc.IsNull);
            Assert.Equal(0ul, offset);
            Assert.False(block.IsEmpty());
        }

        [Fact]
        public void Allocate_AlignedOffsetRespected()
        {
            using var block = MakeBlock(65536);
            block.Allocate(new VmaVirtualAllocationCreateInfo { Size = 100 }, out _, out _);
            block.Allocate(
                new VmaVirtualAllocationCreateInfo { Size = 64, Alignment = 256 },
                out _, out var off2);
            Assert.Equal(0ul, off2 % 256);
        }

        [Fact]
        public void GetAllocationInfo_ReturnsStoredFields()
        {
            using var block = MakeBlock(65536);
            block.Allocate(
                new VmaVirtualAllocationCreateInfo { Size = 512, UserData = "abc" },
                out var alloc, out var offset);

            block.GetAllocationInfo(alloc, out var info);
            Assert.Equal(offset, info.Offset);
            Assert.Equal(512ul, info.Size);
            Assert.Equal("abc", info.UserData);
        }

        [Fact]
        public void SetAllocationUserData_Updates()
        {
            using var block = MakeBlock(4096);
            block.Allocate(
                new VmaVirtualAllocationCreateInfo { Size = 128, UserData = "old" },
                out var alloc, out _);

            block.SetAllocationUserData(alloc, "new");
            block.GetAllocationInfo(alloc, out var info);
            Assert.Equal("new", info.UserData);
        }

        [Fact]
        public void Free_NullAllocation_IsNoOp()
        {
            using var block = MakeBlock(4096);
            block.Free(VmaVirtualAllocation.Null); // no throw
            Assert.True(block.IsEmpty());
        }

        [Fact]
        public void Free_LiveAllocation_RestoresSpace()
        {
            using var block = MakeBlock(4096);
            block.Allocate(new VmaVirtualAllocationCreateInfo { Size = 1024 },
                out var alloc, out _);
            Assert.False(block.IsEmpty());

            block.Free(alloc);
            Assert.True(block.IsEmpty());
        }

        [Fact]
        public void Clear_ReleasesAllAllocations()
        {
            using var block = MakeBlock(65536);
            block.Allocate(new VmaVirtualAllocationCreateInfo { Size = 1024 }, out _, out _);
            block.Allocate(new VmaVirtualAllocationCreateInfo { Size = 2048 }, out _, out _);
            block.Allocate(new VmaVirtualAllocationCreateInfo { Size = 4096 }, out _, out _);
            Assert.False(block.IsEmpty());

            block.Clear();
            Assert.True(block.IsEmpty());
        }

        [Fact]
        public void OperationAfterDispose_Throws()
        {
            var block = MakeBlock(4096);
            block.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                block.Allocate(new VmaVirtualAllocationCreateInfo { Size = 128 }, out _, out _));
        }
    }

    public sealed class VmaVirtualBlockLinearTests
    {
        [Fact]
        public void Linear_UpperAddressAllocation_Succeeds()
        {
            var info = new VmaVirtualBlockCreateInfo
            {
                Size = 65536,
                Flags = VmaVirtualBlockCreateFlags.LinearAlgorithmBit,
            };
            VmaVirtualBlock.Create(in info, out var block);
            using var b = block!;

            Result r = b.Allocate(
                new VmaVirtualAllocationCreateInfo
                {
                    Size = 1024,
                    Flags = VmaVirtualAllocationCreateFlags.UpperAddressBit,
                },
                out var alloc, out var offset);

            Assert.Equal(Result.Success, r);
            Assert.False(alloc.IsNull);
            Assert.Equal(65536ul - 1024ul, offset);
        }

        [Fact]
        public void Linear_LowerAndUpperDontOverlap()
        {
            var info = new VmaVirtualBlockCreateInfo
            {
                Size = 65536,
                Flags = VmaVirtualBlockCreateFlags.LinearAlgorithmBit,
            };
            VmaVirtualBlock.Create(in info, out var block);
            using var b = block!;

            b.Allocate(new VmaVirtualAllocationCreateInfo { Size = 16384 },
                out _, out var loOff);
            b.Allocate(
                new VmaVirtualAllocationCreateInfo
                {
                    Size = 16384,
                    Flags = VmaVirtualAllocationCreateFlags.UpperAddressBit,
                },
                out _, out var hiOff);

            Assert.True(hiOff >= loOff + 16384);
        }
    }

    public sealed class VmaVirtualBlockStatisticsTests
    {
        [Fact]
        public void GetStatistics_TracksAllocations()
        {
            var info = new VmaVirtualBlockCreateInfo { Size = 8192 };
            VmaVirtualBlock.Create(in info, out var block);
            using var b = block!;

            b.Allocate(new VmaVirtualAllocationCreateInfo { Size = 512 }, out _, out _);
            b.Allocate(new VmaVirtualAllocationCreateInfo { Size = 256 }, out _, out _);

            b.GetStatistics(out var stats);
            Assert.Equal(1u, stats.BlockCount);
            Assert.Equal(8192ul, stats.BlockBytes);
            Assert.Equal(2u, stats.AllocationCount);
            Assert.Equal(768ul, stats.AllocationBytes);
        }

        [Fact]
        public void CalculateStatistics_ReportsExtrema()
        {
            var info = new VmaVirtualBlockCreateInfo { Size = 8192 };
            VmaVirtualBlock.Create(in info, out var block);
            using var b = block!;

            b.Allocate(new VmaVirtualAllocationCreateInfo { Size = 256 }, out _, out _);
            b.Allocate(new VmaVirtualAllocationCreateInfo { Size = 1024 }, out _, out _);

            b.CalculateStatistics(out var stats);
            Assert.Equal(2u, stats.Statistics.AllocationCount);
            Assert.Equal(1280ul, stats.Statistics.AllocationBytes);
            Assert.Equal(256ul, stats.AllocationSizeMin);
            Assert.Equal(1024ul, stats.AllocationSizeMax);
        }
    }
}
