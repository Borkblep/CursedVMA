// Tests for the pure-function helpers in CursedVMA.Internal.VmaMath. These
// have no Vulkan dependency, so we can lock them down exhaustively and rely
// on them as building blocks in Phase 4 (TLSF and Linear algorithms).

using CursedVMA.Internal;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaMathAlignTests
    {
        [Theory]
        [InlineData(0ul, 16ul, 0ul)]
        [InlineData(1ul, 16ul, 16ul)]
        [InlineData(15ul, 16ul, 16ul)]
        [InlineData(16ul, 16ul, 16ul)]
        [InlineData(17ul, 16ul, 32ul)]
        [InlineData(31ul, 16ul, 32ul)]
        [InlineData(32ul, 16ul, 32ul)]
        [InlineData(1024ul, 256ul, 1024ul)]
        [InlineData(1025ul, 256ul, 1280ul)]
        [InlineData(0ul, 1ul, 0ul)]
        [InlineData(42ul, 1ul, 42ul)]
        public void AlignUp_ProducesExpectedValue(ulong value, ulong alignment, ulong expected)
        {
            Assert.Equal(expected, VmaMath.AlignUp(value, alignment));
        }

        [Theory]
        [InlineData(0ul, 16ul, 0ul)]
        [InlineData(15ul, 16ul, 0ul)]
        [InlineData(16ul, 16ul, 16ul)]
        [InlineData(17ul, 16ul, 16ul)]
        [InlineData(31ul, 16ul, 16ul)]
        [InlineData(32ul, 16ul, 32ul)]
        [InlineData(1023ul, 256ul, 768ul)]
        [InlineData(1024ul, 256ul, 1024ul)]
        [InlineData(0ul, 1ul, 0ul)]
        [InlineData(42ul, 1ul, 42ul)]
        public void AlignDown_ProducesExpectedValue(ulong value, ulong alignment, ulong expected)
        {
            Assert.Equal(expected, VmaMath.AlignDown(value, alignment));
        }

        [Theory]
        [InlineData(0ul, 1ul, 0ul)]
        [InlineData(1ul, 1ul, 1ul)]
        [InlineData(1ul, 2ul, 1ul)]
        [InlineData(2ul, 2ul, 1ul)]
        [InlineData(3ul, 2ul, 2ul)]
        [InlineData(4ul, 2ul, 2ul)]
        [InlineData(10ul, 3ul, 4ul)]
        [InlineData(11ul, 3ul, 4ul)]
        [InlineData(12ul, 3ul, 4ul)]
        [InlineData(13ul, 3ul, 5ul)]
        public void DivideRoundingUp_ProducesExpectedValue(ulong x, ulong y, ulong expected)
        {
            Assert.Equal(expected, VmaMath.DivideRoundingUp(x, y));
        }
    }

    public sealed class VmaMathPow2Tests
    {
        [Theory]
        [InlineData(0ul, false)]
        [InlineData(1ul, true)]
        [InlineData(2ul, true)]
        [InlineData(3ul, false)]
        [InlineData(4ul, true)]
        [InlineData(5ul, false)]
        [InlineData(7ul, false)]
        [InlineData(8ul, true)]
        [InlineData(255ul, false)]
        [InlineData(256ul, true)]
        [InlineData(1ul << 31, true)]
        [InlineData(1ul << 63, true)]
        [InlineData((1ul << 63) + 1, false)]
        public void IsPow2_ProducesExpectedValue(ulong x, bool expected)
        {
            Assert.Equal(expected, VmaMath.IsPow2(x));
        }

        [Theory]
        [InlineData(0ul, 1ul)]
        [InlineData(1ul, 1ul)]
        [InlineData(2ul, 2ul)]
        [InlineData(3ul, 4ul)]
        [InlineData(4ul, 4ul)]
        [InlineData(5ul, 8ul)]
        [InlineData(7ul, 8ul)]
        [InlineData(8ul, 8ul)]
        [InlineData(9ul, 16ul)]
        [InlineData(1023ul, 1024ul)]
        [InlineData(1024ul, 1024ul)]
        [InlineData(1025ul, 2048ul)]
        public void NextPow2_ProducesExpectedValue(ulong value, ulong expected)
        {
            Assert.Equal(expected, VmaMath.NextPow2(value));
        }

        [Theory]
        [InlineData(0ul, 0ul)]
        [InlineData(1ul, 1ul)]
        [InlineData(2ul, 2ul)]
        [InlineData(3ul, 2ul)]
        [InlineData(4ul, 4ul)]
        [InlineData(5ul, 4ul)]
        [InlineData(7ul, 4ul)]
        [InlineData(8ul, 8ul)]
        [InlineData(1024ul, 1024ul)]
        [InlineData(1025ul, 1024ul)]
        [InlineData(1ul << 63, 1ul << 63)]
        [InlineData((1ul << 63) | 1ul, 1ul << 63)]
        public void PrevPow2_ProducesExpectedValue(ulong value, ulong expected)
        {
            Assert.Equal(expected, VmaMath.PrevPow2(value));
        }
    }

    public sealed class VmaMathBitScanTests
    {
        [Theory]
        [InlineData(0u, -1)]
        [InlineData(1u, 0)]
        [InlineData(2u, 1)]
        [InlineData(3u, 0)]
        [InlineData(0b10000u, 4)]
        [InlineData(0x80000000u, 31)]
        [InlineData(0xFFFFFFFFu, 0)]
        public void BitScanLSB_Uint_ProducesExpectedValue(uint mask, int expected)
        {
            Assert.Equal(expected, VmaMath.BitScanLSB(mask));
        }

        [Theory]
        [InlineData(0u, -1)]
        [InlineData(1u, 0)]
        [InlineData(2u, 1)]
        [InlineData(3u, 1)]
        [InlineData(0b10000u, 4)]
        [InlineData(0x80000000u, 31)]
        [InlineData(0xFFFFFFFFu, 31)]
        public void BitScanMSB_Uint_ProducesExpectedValue(uint mask, int expected)
        {
            Assert.Equal(expected, VmaMath.BitScanMSB(mask));
        }

        [Theory]
        [InlineData(0ul, -1)]
        [InlineData(1ul, 0)]
        [InlineData(1ul << 33, 33)]
        [InlineData(1ul << 63, 63)]
        public void BitScanLSB_Ulong_ProducesExpectedValue(ulong mask, int expected)
        {
            Assert.Equal(expected, VmaMath.BitScanLSB(mask));
        }

        [Theory]
        [InlineData(0ul, -1)]
        [InlineData(1ul, 0)]
        [InlineData(0xFFFFFFFFul, 31)]
        [InlineData(1ul << 33, 33)]
        [InlineData(1ul << 63, 63)]
        [InlineData(ulong.MaxValue, 63)]
        public void BitScanMSB_Ulong_ProducesExpectedValue(ulong mask, int expected)
        {
            Assert.Equal(expected, VmaMath.BitScanMSB(mask));
        }
    }

    public sealed class VmaMathBlocksOnSamePageTests
    {
        // page size 1024, two adjacent 256-byte resources in the same page
        [Fact]
        public void TwoResourcesInSamePage_ReturnsTrue()
        {
            Assert.True(VmaMath.BlocksOnSamePage(
                resourceAOffset: 0, resourceASize: 256,
                resourceBOffset: 256, pageSize: 1024));
        }

        // resource A ends at byte 1023 (last byte of page 0); B starts at 1024 (page 1)
        [Fact]
        public void ResourceAEndsAtPageBoundary_NextResourceOnNextPage_ReturnsFalse()
        {
            Assert.False(VmaMath.BlocksOnSamePage(
                resourceAOffset: 0, resourceASize: 1024,
                resourceBOffset: 1024, pageSize: 1024));
        }

        // resource A ends at byte 1024 (page 1); B starts at 2048 (page 2)
        [Fact]
        public void ResourcesOnDifferentPages_ReturnsFalse()
        {
            Assert.False(VmaMath.BlocksOnSamePage(
                resourceAOffset: 0, resourceASize: 2000,
                resourceBOffset: 2048, pageSize: 1024));
        }

        // resource A's last byte and B's first byte in the same upper page
        [Fact]
        public void BothInUpperPage_ReturnsTrue()
        {
            Assert.True(VmaMath.BlocksOnSamePage(
                resourceAOffset: 2048, resourceASize: 100,
                resourceBOffset: 2200, pageSize: 1024));
        }
    }

    public sealed class VmaMathBufferImageGranularityConflictTests
    {
        // The conflict table from VMA's IsBufferImageGranularityConflict. Parameters are
        // bytes (rather than the internal VmaSuballocationType enum) because xUnit requires
        // test classes to be public and a public method cannot declare an internal
        // parameter type (C# CS0051) — independent of InternalsVisibleTo.
        [Theory]
        [InlineData(/*Free*/         0, /*Buffer*/         2, false)]
        [InlineData(/*Free*/         0, /*ImageOptimal*/   5, false)]
        [InlineData(/*Unknown*/      1, /*Buffer*/         2, true)]
        [InlineData(/*Unknown*/      1, /*ImageOptimal*/   5, true)]
        [InlineData(/*Buffer*/       2, /*Buffer*/         2, false)]
        [InlineData(/*Buffer*/       2, /*ImageLinear*/    4, false)]
        [InlineData(/*Buffer*/       2, /*ImageUnknown*/   3, true)]
        [InlineData(/*Buffer*/       2, /*ImageOptimal*/   5, true)]
        [InlineData(/*ImageLinear*/  4, /*ImageLinear*/    4, false)]
        [InlineData(/*ImageLinear*/  4, /*ImageOptimal*/   5, true)]
        [InlineData(/*ImageOptimal*/ 5, /*ImageOptimal*/   5, false)]
        [InlineData(/*ImageUnknown*/ 3, /*ImageLinear*/    4, true)]
        [InlineData(/*ImageUnknown*/ 3, /*ImageOptimal*/   5, true)]
        public void Conflict_ProducesExpectedValue(byte rawA, byte rawB, bool expected)
        {
            VmaSuballocationType a = (VmaSuballocationType)rawA;
            VmaSuballocationType b = (VmaSuballocationType)rawB;

            // Helper should be symmetric: the argument order must not affect the outcome.
            Assert.Equal(expected, VmaMath.IsBufferImageGranularityConflict(a, b));
            Assert.Equal(expected, VmaMath.IsBufferImageGranularityConflict(b, a));
        }
    }
}
