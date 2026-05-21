// Tests for VmaBlockMetadataLinear: single-stack, double-stack, ring-buffer
// transitions, statistics, and the Validate invariant.

using CursedVMA.Internal;
using CursedVMA.Internal.Algorithms;
using System;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaBlockMetadataLinearBasicTests
    {
        private static VmaBlockMetadataLinear MakeBlock(ulong size, ulong granularity = 1)
        {
            var b = new VmaBlockMetadataLinear(granularity, isVirtual: true);
            b.Init(size);
            return b;
        }

        [Fact]
        public void FreshBlock_IsEmptyAndFullyFree()
        {
            var b = MakeBlock(65536);
            Assert.True(b.IsEmpty());
            Assert.Equal(0u, b.GetAllocationCount());
            Assert.Equal(65536ul, b.GetSumFreeSize());
            Assert.Equal(0ul, b.GetAllocationListBegin());
            Assert.True(b.Validate());
        }

        [Fact]
        public void SingleAlloc_ProducesCorrectHandle()
        {
            var b = MakeBlock(65536);
            bool ok = b.CreateAllocationRequest(
                512, 1, false, VmaSuballocationType.Buffer, 0, out var req);
            Assert.True(ok);
            Assert.Equal(VmaAllocationRequestType.EndOf1st, req.Type);
            b.Alloc(req, VmaSuballocationType.Buffer, null);

            Assert.Equal(1u, b.GetAllocationCount());
            Assert.Equal(65536ul - 512ul, b.GetSumFreeSize());
            Assert.False(b.IsEmpty());

            ulong handle = b.GetAllocationListBegin();
            Assert.NotEqual(0ul, handle);
            Assert.Equal(0ul, b.GetAllocationOffset(handle));
            Assert.True(b.Validate());
        }

        [Fact]
        public void TwoSequentialAllocs_OffsetsMustNotOverlap()
        {
            var b = MakeBlock(65536);

            b.CreateAllocationRequest(256, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);

            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            Assert.Equal(2u, b.GetAllocationCount());

            ulong h1 = b.GetAllocationListBegin();
            ulong h2 = b.GetNextAllocation(h1);
            Assert.NotEqual(0ul, h2);
            Assert.Equal(0ul, b.GetAllocationOffset(h1));
            Assert.Equal(256ul, b.GetAllocationOffset(h2));
            Assert.Equal(0ul, b.GetNextAllocation(h2));
            Assert.True(b.Validate());
        }

        [Fact]
        public void AlignedAlloc_StartsAtAlignedOffset()
        {
            var b = MakeBlock(65536);

            // First alloc is 100 bytes; second needs 256-byte alignment.
            b.CreateAllocationRequest(100, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);

            b.CreateAllocationRequest(64, 256, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            ulong h2 = b.GetNextAllocation(b.GetAllocationListBegin());
            ulong off2 = b.GetAllocationOffset(h2);
            Assert.Equal(0ul, off2 % 256);
            Assert.True(b.Validate());
        }

        [Fact]
        public void FreeFirstAlloc_MakesItInvisibleToIteration()
        {
            var b = MakeBlock(65536);

            b.CreateAllocationRequest(256, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(256, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            ulong h1 = b.GetAllocationListBegin();
            b.Free(h1);

            Assert.Equal(1u, b.GetAllocationCount());
            ulong h2 = b.GetAllocationListBegin();
            Assert.Equal(256ul, b.GetAllocationOffset(h2));
            Assert.True(b.Validate());
        }

        [Fact]
        public void FreeLastAlloc_CompactsVector()
        {
            var b = MakeBlock(65536);

            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            ulong h2 = b.GetNextAllocation(b.GetAllocationListBegin());
            b.Free(h2);

            Assert.Equal(1u, b.GetAllocationCount());
            Assert.Equal(65536ul - 512ul, b.GetSumFreeSize());
            Assert.True(b.Validate());
        }

        [Fact]
        public void FreeAllAllocs_RestoresFullFreeState()
        {
            var b = MakeBlock(65536);

            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, "A");
            b.CreateAllocationRequest(2048, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, "B");

            b.Free(r1.AllocHandle);
            b.Free(r2.AllocHandle);

            Assert.True(b.IsEmpty());
            Assert.Equal(65536ul, b.GetSumFreeSize());
            Assert.True(b.Validate());
        }

        [Fact]
        public void UserData_CanBeRetrievedAndReplaced()
        {
            var b = MakeBlock(65536);
            b.CreateAllocationRequest(256, 1, false, VmaSuballocationType.Buffer, 0, out var req);
            b.Alloc(req, VmaSuballocationType.Buffer, "original");

            ulong h = b.GetAllocationListBegin();
            Assert.Equal("original", b.GetAllocationUserData(h));

            b.SetAllocationUserData(h, "updated");
            Assert.Equal("updated", b.GetAllocationUserData(h));
        }

        [Fact]
        public void GetAllocationInfo_ReturnsCorrectFields()
        {
            var b = MakeBlock(65536);
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var req);
            b.Alloc(req, VmaSuballocationType.Buffer, "myData");

            ulong h = b.GetAllocationListBegin();
            b.GetAllocationInfo(h, out var info);
            Assert.Equal(0ul, info.Offset);
            Assert.Equal(512ul, info.Size);
            Assert.Equal("myData", info.UserData);
        }

        [Fact]
        public void Clear_ResetsToInitialState()
        {
            var b = MakeBlock(65536);
            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var req);
            b.Alloc(req, VmaSuballocationType.Buffer, null);

            b.Clear();

            Assert.True(b.IsEmpty());
            Assert.Equal(65536ul, b.GetSumFreeSize());
            Assert.Equal(0ul, b.GetAllocationListBegin());
            Assert.True(b.Validate());
        }

        [Fact]
        public void RequestBeyondBlockSize_ReturnsFalse()
        {
            var b = MakeBlock(1024);
            bool ok = b.CreateAllocationRequest(2048, 1, false, VmaSuballocationType.Buffer, 0, out _);
            Assert.False(ok);
        }

        [Fact]
        public void RequestWhenFull_ReturnsFalse()
        {
            var b = MakeBlock(512);
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var req);
            b.Alloc(req, VmaSuballocationType.Buffer, null);

            bool ok = b.CreateAllocationRequest(1, 1, false, VmaSuballocationType.Buffer, 0, out _);
            Assert.False(ok);
        }

        [Fact]
        public void GetNextFreeRegionSize_ReturnsGapAfterAllocation()
        {
            var b = MakeBlock(4096);
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            ulong h1 = b.GetAllocationListBegin();
            // After h1 (offset 0, size 512) the next live alloc is at 512, so free gap = 0.
            Assert.Equal(0ul, b.GetNextFreeRegionSize(h1));

            ulong h2 = b.GetNextAllocation(h1);
            // After h2 (offset 512, size 512) the remaining block is free (size 3072).
            Assert.Equal(3072ul, b.GetNextFreeRegionSize(h2));
        }
    }

    public sealed class VmaBlockMetadataLinearDoubleStackTests
    {
        private static VmaBlockMetadataLinear MakeBlock(ulong size)
        {
            var b = new VmaBlockMetadataLinear(1, isVirtual: true);
            b.Init(size);
            return b;
        }

        [Fact]
        public void UpperAddress_AllocatesFromTop()
        {
            var b = MakeBlock(65536);
            bool ok = b.CreateAllocationRequest(
                1024, 1, upperAddress: true, VmaSuballocationType.Buffer, 0, out var req);
            Assert.True(ok);
            Assert.Equal(VmaAllocationRequestType.UpperAddress, req.Type);
            b.Alloc(req, VmaSuballocationType.Buffer, null);

            ulong h = b.GetAllocationListBegin();
            Assert.Equal(65536ul - 1024ul, b.GetAllocationOffset(h));
            Assert.True(b.Validate());
        }

        [Fact]
        public void LowerAndUpperAllocs_DoNotOverlap()
        {
            var b = MakeBlock(65536);

            // Lower allocation: offset 0.
            b.CreateAllocationRequest(16384, 1, false, VmaSuballocationType.Buffer, 0, out var rLo);
            b.Alloc(rLo, VmaSuballocationType.Buffer, null);

            // Upper allocation: from the top.
            b.CreateAllocationRequest(16384, 1, true, VmaSuballocationType.Buffer, 0, out var rHi);
            b.Alloc(rHi, VmaSuballocationType.Buffer, null);

            ulong hLo = b.GetAllocationListBegin();
            ulong hHi = b.GetNextAllocation(hLo);

            ulong loStart = b.GetAllocationOffset(hLo);
            ulong loEnd = loStart + 16384;
            ulong hiStart = b.GetAllocationOffset(hHi);

            Assert.True(hiStart >= loEnd, "Lower and upper allocations overlap.");
            Assert.Equal(2u, b.GetAllocationCount());
            Assert.True(b.Validate());
        }

        [Fact]
        public void UpperAddressExceedsLower_ReturnsFalse()
        {
            var b = MakeBlock(1024);

            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var rLo);
            b.Alloc(rLo, VmaSuballocationType.Buffer, null);

            b.CreateAllocationRequest(512, 1, true, VmaSuballocationType.Buffer, 0, out var rHi);
            b.Alloc(rHi, VmaSuballocationType.Buffer, null);

            // Block is now full; both directions fail.
            bool lo = b.CreateAllocationRequest(1, 1, false, VmaSuballocationType.Buffer, 0, out _);
            bool hi = b.CreateAllocationRequest(1, 1, true, VmaSuballocationType.Buffer, 0, out _);
            Assert.False(lo);
            Assert.False(hi);
        }

        [Fact]
        public void FreeUpperAlloc_RestoresFreeSpace()
        {
            var b = MakeBlock(65536);
            b.CreateAllocationRequest(1024, 1, true, VmaSuballocationType.Buffer, 0, out var req);
            b.Alloc(req, VmaSuballocationType.Buffer, null);

            ulong h = b.GetAllocationListBegin();
            b.Free(h);

            Assert.True(b.IsEmpty());
            Assert.Equal(65536ul, b.GetSumFreeSize());
            Assert.True(b.Validate());
        }
    }

    public sealed class VmaBlockMetadataLinearStatisticsTests
    {
        [Fact]
        public void AddStatistics_EmptyBlock_OnlyCountsBlock()
        {
            var b = new VmaBlockMetadataLinear(1, isVirtual: true);
            b.Init(4096);

            var stats = new VmaStatistics();
            b.AddStatistics(ref stats);

            Assert.Equal(1u, stats.BlockCount);
            Assert.Equal(4096ul, stats.BlockBytes);
            Assert.Equal(0u, stats.AllocationCount);
            Assert.Equal(0ul, stats.AllocationBytes);
        }

        [Fact]
        public void AddStatistics_AfterAlloc_ReflectsAllocatedBytes()
        {
            var b = new VmaBlockMetadataLinear(1, isVirtual: true);
            b.Init(4096);
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var req);
            b.Alloc(req, VmaSuballocationType.Buffer, null);

            var stats = new VmaStatistics();
            b.AddStatistics(ref stats);

            Assert.Equal(1u, stats.AllocationCount);
            Assert.Equal(512ul, stats.AllocationBytes);
            Assert.Equal(4096ul - 512ul, b.GetSumFreeSize());
        }

        [Fact]
        public void AddDetailedStatistics_EmptyBlock_OneUnusedRange()
        {
            var b = new VmaBlockMetadataLinear(1, isVirtual: true);
            b.Init(4096);

            var stats = new VmaDetailedStatistics();
            b.AddDetailedStatistics(ref stats);

            Assert.Equal(1u, stats.Statistics.BlockCount);
            Assert.Equal(0u, stats.Statistics.AllocationCount);
            Assert.Equal(1u, stats.UnusedRangeCount);
            Assert.Equal(4096ul, stats.UnusedRangeSizeMin);
            Assert.Equal(4096ul, stats.UnusedRangeSizeMax);
        }

        [Fact]
        public void AddDetailedStatistics_TwoAllocs_CorrectCounts()
        {
            var b = new VmaBlockMetadataLinear(1, isVirtual: true);
            b.Init(4096);
            b.CreateAllocationRequest(256, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            var stats = new VmaDetailedStatistics();
            b.AddDetailedStatistics(ref stats);

            Assert.Equal(2u, stats.Statistics.AllocationCount);
            Assert.Equal(768ul, stats.Statistics.AllocationBytes);
            Assert.Equal(256ul, stats.AllocationSizeMin);
            Assert.Equal(512ul, stats.AllocationSizeMax);
            // One trailing unused range after the two allocations.
            Assert.Equal(1u, stats.UnusedRangeCount);
        }
    }

    public sealed class VmaBlockMetadataLinearRingBufferTests
    {
        [Fact]
        public void RingBuffer_PromotesVectorsWhenFirstIsExhausted()
        {
            // Allocate two items, free the first so ring-buffer mode can kick in,
            // then verify a new alloc at the front succeeds.
            var b = new VmaBlockMetadataLinear(1, isVirtual: true);
            b.Init(4096);

            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, "first");
            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, "second");
            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r3);
            b.Alloc(r3, VmaSuballocationType.Buffer, "third");

            // Free from the front to trigger the ring-buffer mode pathway for future allocs.
            b.Free(r1.AllocHandle);
            b.Free(r2.AllocHandle);

            // Should now be able to allocate in the freed region (EndOf2nd / ring path).
            bool ok = b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r4);
            Assert.True(ok);
            b.Alloc(r4, VmaSuballocationType.Buffer, "wrapped");

            Assert.Equal(2u, b.GetAllocationCount());
            Assert.True(b.Validate());
        }
    }
}
