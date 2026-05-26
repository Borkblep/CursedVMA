// Phase 24c metadata-level tests: ring-buffer wrap offsets, double-stack
// full-block touching, buffer-image granularity padding, and the TLSF
// strategy-bit deviation doc test.

using CursedVMA.Internal;
using CursedVMA.Internal.Algorithms;
using Xunit;

namespace CursedVMA.Tests
{
    // ── Linear ring-buffer wrap correctness ─────────────────────────────────

    public sealed class VmaLinearRingBufferWrapTests
    {
        private static VmaBlockMetadataLinear Make(ulong size)
        {
            var b = new VmaBlockMetadataLinear(1, isVirtual: true);
            b.Init(size);
            return b;
        }

        [Fact]
        public void Wrap_NewAllocationDoesNotOverlapStillLiveAllocation()
        {
            var b = Make(4096);

            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, "alive");
            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, "freed");
            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r3);
            b.Alloc(r3, VmaSuballocationType.Buffer, "alive2");

            // Free a middle/end region to set up ring-buffer wrap eligibility.
            b.Free(r2.AllocHandle);
            b.Free(r3.AllocHandle);

            // Allocate again; must not overlap r1 (still live at offset 0..1024).
            bool ok = b.CreateAllocationRequest(
                512, 1, false, VmaSuballocationType.Buffer, 0, out var r4);
            Assert.True(ok);
            b.Alloc(r4, VmaSuballocationType.Buffer, "wrapped");

            ulong r1Off  = b.GetAllocationOffset(r1.AllocHandle);
            ulong r4Off  = b.GetAllocationOffset(r4.AllocHandle);
            ulong r1End  = r1Off + 1024;
            ulong r4End  = r4Off + 512;

            // Either r4 ends before r1 starts or r4 starts at/after r1's end.
            Assert.True(r4End <= r1Off || r4Off >= r1End,
                $"r1 [{r1Off}..{r1End}) overlaps r4 [{r4Off}..{r4End})");
            Assert.True(b.Validate());
        }

        [Fact]
        public void TailExhausted_FrontFreed_NextAlloc_KnownDeviation_DoesNotWrap()
        {
            // VMA C++ supports a true ring-buffer mode in
            // VmaBlockMetadataLinear where, once the tail is exhausted and
            // the head has been freed, subsequent allocations wrap to the
            // freed front region. CursedVMA's port does not implement the
            // wrap promotion: the linear algorithm is forward-only, and
            // freed front slots are never reclaimed for new allocations.
            //
            // This test pins the current behavior so a future implementation
            // either restores parity (and updates this test) or stays
            // consistent.
            var b = Make(4096);

            b.CreateAllocationRequest(1024, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(3072, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);
            b.Free(r1.AllocHandle);

            // Per VMA C++ this would succeed by wrapping. CursedVMA returns
            // false: the front-freed region is not reclaimed.
            bool ok = b.CreateAllocationRequest(
                512, 1, false, VmaSuballocationType.Buffer, 0, out _);
            Assert.False(ok);
            Assert.True(b.Validate());
        }
    }

    // ── Linear double-stack boundary behavior ───────────────────────────────

    public sealed class VmaLinearDoubleStackBoundaryTests
    {
        [Fact]
        public void FullBlock_BothStacksMeet_NoGap()
        {
            // Lower 32768 + upper 32768 = exactly fill 65536.
            var b = new VmaBlockMetadataLinear(1, isVirtual: true);
            b.Init(65536);

            b.CreateAllocationRequest(32768, 1, false, VmaSuballocationType.Buffer, 0, out var rLo);
            b.Alloc(rLo, VmaSuballocationType.Buffer, null);

            bool ok = b.CreateAllocationRequest(
                32768, 1, upperAddress: true, VmaSuballocationType.Buffer, 0, out var rHi);
            Assert.True(ok);
            b.Alloc(rHi, VmaSuballocationType.Buffer, null);

            ulong loEnd = b.GetAllocationOffset(rLo.AllocHandle) + 32768;
            ulong hiStart = b.GetAllocationOffset(rHi.AllocHandle);
            Assert.Equal(loEnd, hiStart);
            Assert.Equal(0ul, b.GetSumFreeSize());
            Assert.True(b.Validate());
        }

        [Fact]
        public void FreeFromTop_CanReallocateFromTop()
        {
            var b = new VmaBlockMetadataLinear(1, isVirtual: true);
            b.Init(65536);

            b.CreateAllocationRequest(1024, 1, true, VmaSuballocationType.Buffer, 0, out var rHi1);
            b.Alloc(rHi1, VmaSuballocationType.Buffer, null);
            ulong firstOff = b.GetAllocationOffset(rHi1.AllocHandle);
            b.Free(rHi1.AllocHandle);

            bool ok = b.CreateAllocationRequest(
                1024, 1, true, VmaSuballocationType.Buffer, 0, out var rHi2);
            Assert.True(ok);
            b.Alloc(rHi2, VmaSuballocationType.Buffer, null);
            ulong secondOff = b.GetAllocationOffset(rHi2.AllocHandle);

            Assert.Equal(firstOff, secondOff);
            Assert.True(b.Validate());
        }
    }

    // ── Buffer/image granularity at metadata level ─────────────────────────

    public sealed class VmaTlsfGranularityTests
    {
        [Fact]
        public void BufferThenImage_OffsetAlignedToGranularity_WhenGranularityGreaterThanOne()
        {
            // Granularity 256 forces an image suballocation after a buffer to
            // align to 256 even if the buffer ended at a non-aligned offset.
            var b = new VmaBlockMetadataTlsf(bufferImageGranularity: 256, isVirtual: false);
            b.Init(8192);

            // Buffer of 100 bytes ends at offset 100 (non-aligned to 256).
            b.CreateAllocationRequest(100, 1, false, VmaSuballocationType.Buffer, 0, out var rBuf);
            b.Alloc(rBuf, VmaSuballocationType.Buffer, null);

            // Image suballocation must skip to the next 256-aligned offset.
            bool ok = b.CreateAllocationRequest(
                256, 1, false, VmaSuballocationType.ImageOptimal, 0, out var rImg);
            Assert.True(ok);
            b.Alloc(rImg, VmaSuballocationType.ImageOptimal, null);

            ulong imgOff = b.GetAllocationOffset(rImg.AllocHandle);
            Assert.True(imgOff >= 256, $"Image offset {imgOff} not granularity-aligned");
            Assert.Equal(0ul, imgOff % 256);
            Assert.True(b.Validate());
        }

        [Fact]
        public void Granularity1_NoPaddingBetweenBufferAndImage()
        {
            // Granularity 1 means buffer/image can pack tightly.
            var b = new VmaBlockMetadataTlsf(bufferImageGranularity: 1, isVirtual: false);
            b.Init(8192);

            b.CreateAllocationRequest(100, 1, false, VmaSuballocationType.Buffer, 0, out var rBuf);
            b.Alloc(rBuf, VmaSuballocationType.Buffer, null);
            ulong bufOff = b.GetAllocationOffset(rBuf.AllocHandle);

            b.CreateAllocationRequest(64, 1, false, VmaSuballocationType.ImageOptimal, 0, out var rImg);
            b.Alloc(rImg, VmaSuballocationType.ImageOptimal, null);
            ulong imgOff = b.GetAllocationOffset(rImg.AllocHandle);

            Assert.Equal(bufOff + 100, imgOff);
            Assert.True(b.Validate());
        }

        [Fact]
        public void SameTypeAllocations_NoGranularityPaddingApplied()
        {
            // Two buffers in a row should pack tightly regardless of
            // granularity — the conflict rule only fires across types.
            var b = new VmaBlockMetadataTlsf(bufferImageGranularity: 256, isVirtual: false);
            b.Init(8192);

            b.CreateAllocationRequest(100, 1, false, VmaSuballocationType.Buffer, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Buffer, null);
            b.CreateAllocationRequest(100, 1, false, VmaSuballocationType.Buffer, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Buffer, null);

            ulong off1 = b.GetAllocationOffset(r1.AllocHandle);
            ulong off2 = b.GetAllocationOffset(r2.AllocHandle);
            Assert.Equal(off1 + 100, off2);
            Assert.True(b.Validate());
        }
    }

    // ── TLSF strategy bits: documented deviation ───────────────────────────

    public sealed class VmaTlsfStrategyDeviationTests
    {
        // VMA C++ honors StrategyMinMemoryBit/MinTimeBit/MinOffsetBit by
        // selecting different free-block search strategies. CursedVMA's
        // TLSF accepts the strategy parameter but does not differentiate;
        // every strategy bit produces the same first-suitable result.
        //
        // This test pins that behavior so a future change either implements
        // the differentiation (and updates this test) or stays consistent.

        private static VmaBlockMetadataTlsf MakeFragmented(ulong size, ulong holeSize)
        {
            // Create a small TLSF block with one large free hole at the
            // beginning and one small free hole later, by allocating then
            // freeing specific regions.
            var b = new VmaBlockMetadataTlsf(1, isVirtual: true);
            b.Init(size);
            // Two large allocs then free the first → leaves a hole at the
            // start big enough for `holeSize`.
            b.CreateAllocationRequest(holeSize, 1, false, VmaSuballocationType.Unknown, 0, out var r1);
            b.Alloc(r1, VmaSuballocationType.Unknown, null);
            b.CreateAllocationRequest(holeSize * 2, 1, false, VmaSuballocationType.Unknown, 0, out var r2);
            b.Alloc(r2, VmaSuballocationType.Unknown, null);
            b.Free(r1.AllocHandle);
            return b;
        }

        [Fact]
        public void AllStrategyBits_ProduceSameOffset_KnownDeviation()
        {
            const uint StrategyMinMemoryBit = 0x00010000;
            const uint StrategyMinTimeBit   = 0x00020000;
            const uint StrategyMinOffsetBit = 0x00040000;

            var bMem  = MakeFragmented(65536, 1024);
            var bTime = MakeFragmented(65536, 1024);
            var bOff  = MakeFragmented(65536, 1024);

            bMem.CreateAllocationRequest(
                512, 1, false, VmaSuballocationType.Unknown, StrategyMinMemoryBit, out var rMem);
            bTime.CreateAllocationRequest(
                512, 1, false, VmaSuballocationType.Unknown, StrategyMinTimeBit, out var rTime);
            bOff.CreateAllocationRequest(
                512, 1, false, VmaSuballocationType.Unknown, StrategyMinOffsetBit, out var rOff);

            bMem.Alloc(rMem,   VmaSuballocationType.Unknown, null);
            bTime.Alloc(rTime, VmaSuballocationType.Unknown, null);
            bOff.Alloc(rOff,   VmaSuballocationType.Unknown, null);

            // Same offset across all three strategy bits.
            ulong offMem  = bMem .GetAllocationOffset(rMem.AllocHandle);
            ulong offTime = bTime.GetAllocationOffset(rTime.AllocHandle);
            ulong offOff  = bOff .GetAllocationOffset(rOff.AllocHandle);
            Assert.Equal(offMem, offTime);
            Assert.Equal(offMem, offOff);
        }
    }
}
