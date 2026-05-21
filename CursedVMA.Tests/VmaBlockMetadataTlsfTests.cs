// Tests for VmaBlockMetadataTlsf: TLSF block split/coalesce, alignment,
// statistics, and the Validate invariant.

using CursedVMA.Internal;
using CursedVMA.Internal.Algorithms;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaBlockMetadataTlsfBasicTests
    {
        private static VmaBlockMetadataTlsf MakeBlock(ulong size, ulong granularity = 1)
        {
            var b = new VmaBlockMetadataTlsf(granularity, isVirtual: true);
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
        public void SingleAlloc_ProducesCorrectOffsetAndHandle()
        {
            var b = MakeBlock(65536);
            bool ok = b.CreateAllocationRequest(
                512, 1, false, VmaSuballocationType.Buffer, 0, out var req);
            Assert.True(ok);
            Assert.Equal(VmaAllocationRequestType.TLSF, req.Type);
            b.Alloc(req, VmaSuballocationType.Buffer, null);

            Assert.Equal(1u, b.GetAllocationCount());
            Assert.Equal(65536ul - 512ul, b.GetSumFreeSize());

            ulong h = b.GetAllocationListBegin();
            Assert.NotEqual(0ul, h);
            Assert.Equal(0ul, b.GetAllocationOffset(h));
            Assert.True(b.Validate());
        }

        [Fact]
        public void TwoAllocs_BothReachableViaIteration()
        {
            var b = MakeBlock(65536);

            b.CreateAllocationRequest(256, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, "A");
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, "B");

            Assert.Equal(2u, b.GetAllocationCount());
            ulong h1 = b.GetAllocationListBegin();
            ulong h2 = b.GetNextAllocation(h1);
            Assert.NotEqual(0ul, h2);
            Assert.Equal(0ul, b.GetNextAllocation(h2));
            Assert.True(b.Validate());
        }

        [Fact]
        public void FreeAndReallocate_ReusesSameSpace()
        {
            var b = MakeBlock(65536);

            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            ulong handle = b.GetAllocationListBegin();
            ulong origOffset = b.GetAllocationOffset(handle);

            b.Free(handle);

            // After freeing, the 1024-byte block should be available again.
            bool ok = b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            Assert.True(ok);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            ulong newHandle = b.GetAllocationListBegin();
            ulong newOffset = b.GetAllocationOffset(newHandle);
            Assert.Equal(origOffset, newOffset);
            Assert.True(b.Validate());
        }

        [Fact]
        public void FreeAllAllocs_RestoresFullFreeState()
        {
            var b = MakeBlock(65536);

            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(2048, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            b.Free(r1.AllocHandle);
            b.Free(r2.AllocHandle);

            Assert.True(b.IsEmpty());
            Assert.Equal(65536ul, b.GetSumFreeSize());
            Assert.Equal(1u, b.GetFreeRegionsCount()); // only the null block
            Assert.True(b.Validate());
        }

        [Fact]
        public void AlignedAlloc_OffsetSatisfiesAlignment()
        {
            var b = MakeBlock(65536);

            // First alloc is 100 bytes (unaligned end at 100).
            b.CreateAllocationRequest(100, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);

            // Second alloc needs 256-byte alignment.
            bool ok = b.CreateAllocationRequest(64, 256, false, VmaSuballocationType.Buffer, 0, out var r2);
            Assert.True(ok);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            ulong h2 = b.GetNextAllocation(b.GetAllocationListBegin());
            Assert.Equal(0ul, b.GetAllocationOffset(h2) % 256);
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
            b.Alloc(req, VmaSuballocationType.Buffer, "payload");

            ulong h = b.GetAllocationListBegin();
            b.GetAllocationInfo(h, out var info);
            Assert.Equal(0ul, info.Offset);
            Assert.Equal(512ul, info.Size);
            Assert.Equal("payload", info.UserData);
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
        public void UpperAddress_ReturnsFalse()
        {
            var b = MakeBlock(65536);
            bool ok = b.CreateAllocationRequest(256, 1, upperAddress: true,
                VmaSuballocationType.Buffer, 0, out _);
            Assert.False(ok);
        }
    }

    public sealed class VmaBlockMetadataTlsfCoalesceTests
    {
        private static VmaBlockMetadataTlsf MakeBlock(ulong size)
        {
            var b = new VmaBlockMetadataTlsf(1, isVirtual: true);
            b.Init(size);
            return b;
        }

        [Fact]
        public void FreeMiddleAlloc_CoalescesWithNeighboursAfterBothFreed()
        {
            var b = MakeBlock(65536);

            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r3);
            b.Alloc(r3, VmaSuballocationType.Buffer, null);

            // Free r1 and r3 first; r2 keeps them separate.
            b.Free(r1.AllocHandle);
            b.Free(r3.AllocHandle);
            Assert.Equal(1u, b.GetAllocationCount());
            Assert.True(b.Validate());

            // Free r2; the two free blocks on either side should now coalesce.
            b.Free(r2.AllocHandle);
            Assert.True(b.IsEmpty());
            // All coalesced → only the null block remains as one free region.
            Assert.Equal(1u, b.GetFreeRegionsCount());
            Assert.Equal(65536ul, b.GetSumFreeSize());
            Assert.True(b.Validate());
        }

        [Fact]
        public void FreeAllInReverseOrder_YieldsOneRegion()
        {
            var b = MakeBlock(65536);

            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r3);
            b.Alloc(r3, VmaSuballocationType.Buffer, null);

            b.Free(r3.AllocHandle);
            b.Free(r2.AllocHandle);
            b.Free(r1.AllocHandle);

            Assert.True(b.IsEmpty());
            Assert.Equal(1u, b.GetFreeRegionsCount());
            Assert.Equal(65536ul, b.GetSumFreeSize());
            Assert.True(b.Validate());
        }

        [Fact]
        public void ManyAllocsAndFrees_ValidateAlwaysPasses()
        {
            var b = MakeBlock(65536);
            var handles = new ulong[8];

            for (int i = 0; i < 8; i++)
            {
                b.CreateAllocationRequest((ulong)(256 * (i + 1)), 1, false,
                    VmaSuballocationType.Buffer, 0, out var req);
                b.Alloc(req, VmaSuballocationType.Buffer, i);
                handles[i] = req.AllocHandle;
            }
            Assert.True(b.Validate());

            // Free in odd positions first.
            for (int i = 1; i < 8; i += 2)
                b.Free(handles[i]);
            Assert.True(b.Validate());

            // Free remaining.
            for (int i = 0; i < 8; i += 2)
                b.Free(handles[i]);
            Assert.True(b.IsEmpty());
            Assert.True(b.Validate());
        }
    }

    public sealed class VmaBlockMetadataTlsfStatisticsTests
    {
        [Fact]
        public void AddStatistics_EmptyBlock_OnlyCountsBlock()
        {
            var b = new VmaBlockMetadataTlsf(1, isVirtual: true);
            b.Init(4096);

            var stats = new VmaStatistics();
            b.AddStatistics(ref stats);

            Assert.Equal(1u, stats.BlockCount);
            Assert.Equal(4096ul, stats.BlockBytes);
            Assert.Equal(0u, stats.AllocationCount);
            Assert.Equal(0ul, stats.AllocationBytes);
        }

        [Fact]
        public void AddDetailedStatistics_TwoAllocs_CorrectCounts()
        {
            var b = new VmaBlockMetadataTlsf(1, isVirtual: true);
            b.Init(4096);
            b.CreateAllocationRequest(256, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(512, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            var stats = new VmaDetailedStatistics();
            b.AddDetailedStatistics(ref stats);

            Assert.Equal(1u, stats.Statistics.BlockCount);
            Assert.Equal(2u, stats.Statistics.AllocationCount);
            Assert.Equal(768ul, stats.Statistics.AllocationBytes);
            Assert.Equal(256ul, stats.AllocationSizeMin);
            Assert.Equal(512ul, stats.AllocationSizeMax);
        }

        [Fact]
        public void AddDetailedStatistics_EmptyBlock_OneUnusedRange()
        {
            var b = new VmaBlockMetadataTlsf(1, isVirtual: true);
            b.Init(4096);

            var stats = new VmaDetailedStatistics();
            b.AddDetailedStatistics(ref stats);

            Assert.Equal(0u, stats.Statistics.AllocationCount);
            Assert.Equal(1u, stats.UnusedRangeCount);
            Assert.Equal(4096ul, stats.UnusedRangeSizeMin);
        }
    }
}
