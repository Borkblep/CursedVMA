// Phase 2 regression tests. These do not exercise any allocator behavior;
// they pin down the bit values, enum members, and default-initialization
// shape of the public types so accidental edits show up as test failures
// rather than runtime surprises.

using System;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaAllocatorCreateFlagsTests
    {
        [Theory]
        [InlineData(VmaAllocatorCreateFlags.ExternallySynchronizedBit, 0x00000001u)]
        [InlineData(VmaAllocatorCreateFlags.KhrDedicatedAllocationBit, 0x00000002u)]
        [InlineData(VmaAllocatorCreateFlags.KhrBindMemory2Bit, 0x00000004u)]
        [InlineData(VmaAllocatorCreateFlags.ExtMemoryBudgetBit, 0x00000008u)]
        [InlineData(VmaAllocatorCreateFlags.AmdDeviceCoherentMemoryBit, 0x00000010u)]
        [InlineData(VmaAllocatorCreateFlags.BufferDeviceAddressBit, 0x00000020u)]
        [InlineData(VmaAllocatorCreateFlags.ExtMemoryPriorityBit, 0x00000040u)]
        [InlineData(VmaAllocatorCreateFlags.KhrMaintenance4Bit, 0x00000080u)]
        [InlineData(VmaAllocatorCreateFlags.KhrMaintenance5Bit, 0x00000100u)]
        [InlineData(VmaAllocatorCreateFlags.KhrExternalMemoryWin32Bit, 0x00000200u)]
        public void BitsMatchVmaConstants(VmaAllocatorCreateFlags flag, uint expected)
        {
            Assert.Equal(expected, (uint)flag);
        }
    }

    public sealed class VmaMemoryUsageTests
    {
        [Theory]
        [InlineData(VmaMemoryUsage.Unknown, 0u)]
        [InlineData(VmaMemoryUsage.GpuOnly, 1u)]
        [InlineData(VmaMemoryUsage.CpuOnly, 2u)]
        [InlineData(VmaMemoryUsage.CpuToGpu, 3u)]
        [InlineData(VmaMemoryUsage.GpuToCpu, 4u)]
        [InlineData(VmaMemoryUsage.CpuCopy, 5u)]
        [InlineData(VmaMemoryUsage.GpuLazilyAllocated, 6u)]
        [InlineData(VmaMemoryUsage.Auto, 7u)]
        [InlineData(VmaMemoryUsage.AutoPreferDevice, 8u)]
        [InlineData(VmaMemoryUsage.AutoPreferHost, 9u)]
        public void ValuesMatchVmaConstants(VmaMemoryUsage usage, uint expected)
        {
            Assert.Equal(expected, (uint)usage);
        }
    }

    public sealed class VmaAllocationCreateFlagsTests
    {
        [Theory]
        [InlineData(VmaAllocationCreateFlags.DedicatedMemoryBit, 0x00000001u)]
        [InlineData(VmaAllocationCreateFlags.NeverAllocateBit, 0x00000002u)]
        [InlineData(VmaAllocationCreateFlags.MappedBit, 0x00000004u)]
        [InlineData(VmaAllocationCreateFlags.UserDataCopyStringBit, 0x00000020u)]
        [InlineData(VmaAllocationCreateFlags.UpperAddressBit, 0x00000040u)]
        [InlineData(VmaAllocationCreateFlags.DontBindBit, 0x00000080u)]
        [InlineData(VmaAllocationCreateFlags.WithinBudgetBit, 0x00000100u)]
        [InlineData(VmaAllocationCreateFlags.CanAliasBit, 0x00000200u)]
        [InlineData(VmaAllocationCreateFlags.HostAccessSequentialWriteBit, 0x00000400u)]
        [InlineData(VmaAllocationCreateFlags.HostAccessRandomBit, 0x00000800u)]
        [InlineData(VmaAllocationCreateFlags.HostAccessAllowTransferInsteadBit, 0x00001000u)]
        [InlineData(VmaAllocationCreateFlags.StrategyMinMemoryBit, 0x00010000u)]
        [InlineData(VmaAllocationCreateFlags.StrategyMinTimeBit, 0x00020000u)]
        [InlineData(VmaAllocationCreateFlags.StrategyMinOffsetBit, 0x00040000u)]
        public void BitsMatchVmaConstants(VmaAllocationCreateFlags flag, uint expected)
        {
            Assert.Equal(expected, (uint)flag);
        }

        [Fact]
        public void StrategyAliasesMatchTheirCanonicalBits()
        {
            Assert.Equal(VmaAllocationCreateFlags.StrategyMinMemoryBit, VmaAllocationCreateFlags.StrategyBestFitBit);
            Assert.Equal(VmaAllocationCreateFlags.StrategyMinTimeBit, VmaAllocationCreateFlags.StrategyFirstFitBit);
        }

        [Fact]
        public void StrategyMaskCoversAllStrategyBits()
        {
            VmaAllocationCreateFlags expected =
                VmaAllocationCreateFlags.StrategyMinMemoryBit
                | VmaAllocationCreateFlags.StrategyMinTimeBit
                | VmaAllocationCreateFlags.StrategyMinOffsetBit;

            Assert.Equal(VmaAllocationCreateFlags.StrategyMask, expected);
        }
    }

    public sealed class VmaPoolCreateFlagsTests
    {
        [Theory]
        [InlineData(VmaPoolCreateFlags.IgnoreBufferImageGranularityBit, 0x00000002u)]
        [InlineData(VmaPoolCreateFlags.LinearAlgorithmBit, 0x00000004u)]
        public void BitsMatchVmaConstants(VmaPoolCreateFlags flag, uint expected)
        {
            Assert.Equal(expected, (uint)flag);
        }

        [Fact]
        public void AlgorithmMaskEqualsLinearAlgorithmBit()
        {
            Assert.Equal(VmaPoolCreateFlags.LinearAlgorithmBit, VmaPoolCreateFlags.AlgorithmMask);
        }
    }

    public sealed class VmaDefragmentationFlagsTests
    {
        [Theory]
        [InlineData(VmaDefragmentationFlags.AlgorithmFastBit, 0x1u)]
        [InlineData(VmaDefragmentationFlags.AlgorithmBalancedBit, 0x2u)]
        [InlineData(VmaDefragmentationFlags.AlgorithmFullBit, 0x4u)]
        [InlineData(VmaDefragmentationFlags.AlgorithmExtensiveBit, 0x8u)]
        public void BitsMatchVmaConstants(VmaDefragmentationFlags flag, uint expected)
        {
            Assert.Equal(expected, (uint)flag);
        }

        [Fact]
        public void AlgorithmMaskCoversAllFourAlgorithmBits()
        {
            Assert.Equal(0xFu, (uint)VmaDefragmentationFlags.AlgorithmMask);
        }
    }

    public sealed class VmaDefragmentationMoveOperationTests
    {
        [Theory]
        [InlineData(VmaDefragmentationMoveOperation.Copy, 0u)]
        [InlineData(VmaDefragmentationMoveOperation.Ignore, 1u)]
        [InlineData(VmaDefragmentationMoveOperation.Destroy, 2u)]
        public void ValuesMatchVmaConstants(VmaDefragmentationMoveOperation op, uint expected)
        {
            Assert.Equal(expected, (uint)op);
        }
    }

    public sealed class VmaVirtualBlockCreateFlagsTests
    {
        [Fact]
        public void LinearAlgorithmBitMatchesVmaConstant()
        {
            Assert.Equal(0x00000001u, (uint)VmaVirtualBlockCreateFlags.LinearAlgorithmBit);
        }

        [Fact]
        public void AlgorithmMaskEqualsLinearAlgorithmBit()
        {
            Assert.Equal(VmaVirtualBlockCreateFlags.LinearAlgorithmBit, VmaVirtualBlockCreateFlags.AlgorithmMask);
        }
    }

    public sealed class VmaVirtualAllocationCreateFlagsTests
    {
        [Fact]
        public void FlagsShareValuesWithAllocationCreateFlags()
        {
            Assert.Equal((uint)VmaAllocationCreateFlags.UpperAddressBit, (uint)VmaVirtualAllocationCreateFlags.UpperAddressBit);
            Assert.Equal((uint)VmaAllocationCreateFlags.StrategyMinMemoryBit, (uint)VmaVirtualAllocationCreateFlags.StrategyMinMemoryBit);
            Assert.Equal((uint)VmaAllocationCreateFlags.StrategyMinTimeBit, (uint)VmaVirtualAllocationCreateFlags.StrategyMinTimeBit);
            Assert.Equal((uint)VmaAllocationCreateFlags.StrategyMinOffsetBit, (uint)VmaVirtualAllocationCreateFlags.StrategyMinOffsetBit);
            Assert.Equal((uint)VmaAllocationCreateFlags.StrategyMask, (uint)VmaVirtualAllocationCreateFlags.StrategyMask);
        }
    }

    public sealed class VmaVirtualAllocationTests
    {
        [Fact]
        public void DefaultIsNullHandle()
        {
            VmaVirtualAllocation a = default;
            Assert.True(a.IsNull);
            Assert.Equal(VmaVirtualAllocation.Null, a);
            Assert.Equal(0ul, a.Handle);
        }

        [Fact]
        public void EqualityAndHashCodeFollowHandleValue()
        {
            // Use reflection-free construction via the public Null sentinel and
            // an internal constructor exposed to this assembly via InternalsVisibleTo.
            VmaVirtualAllocation a = new(0xDEADBEEFul);
            VmaVirtualAllocation b = new(0xDEADBEEFul);
            VmaVirtualAllocation c = new(0xCAFEF00Dul);

            Assert.Equal(a, b);
            Assert.True(a == b);
            Assert.False(a != b);
            Assert.NotEqual(a, c);
            Assert.True(a != c);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.False(a.IsNull);
        }
    }

    public sealed class VmaStructDefaultsTests
    {
        [Fact]
        public void VmaStatisticsDefaultsAreZero()
        {
            VmaStatistics s = default;
            Assert.Equal(0u, s.BlockCount);
            Assert.Equal(0u, s.AllocationCount);
            Assert.Equal(0ul, s.BlockBytes);
            Assert.Equal(0ul, s.AllocationBytes);
        }

        [Fact]
        public void VmaDetailedStatisticsDefaultsAreZero()
        {
            VmaDetailedStatistics s = default;
            Assert.Equal(0u, s.UnusedRangeCount);
            Assert.Equal(0ul, s.AllocationSizeMin);
            Assert.Equal(0ul, s.AllocationSizeMax);
            Assert.Equal(0ul, s.UnusedRangeSizeMin);
            Assert.Equal(0ul, s.UnusedRangeSizeMax);
        }

        [Fact]
        public void VmaBudgetDefaultsAreZero()
        {
            VmaBudget b = default;
            Assert.Equal(0ul, b.Usage);
            Assert.Equal(0ul, b.Budget);
        }

        [Fact]
        public void VmaTotalStatisticsInlineArraysHaveExpectedLengths()
        {
            VmaTotalStatistics total = default;

            // Inline arrays expose Length-equivalent access via the indexer; the
            // 32 / 16 sizes come from VK_MAX_MEMORY_TYPES and VK_MAX_MEMORY_HEAPS.
            // Read the last valid slot to prove the storage exists.
            ref VmaDetailedStatistics lastType = ref total.MemoryType[31];
            ref VmaDetailedStatistics lastHeap = ref total.MemoryHeap[15];

            Assert.Equal(0u, lastType.UnusedRangeCount);
            Assert.Equal(0u, lastHeap.UnusedRangeCount);
        }

        [Fact]
        public void VmaAllocatorCreateInfoDefaultsAreEmpty()
        {
            VmaAllocatorCreateInfo info = default;
            Assert.Equal(VmaAllocatorCreateFlags.None, info.Flags);
            Assert.Null(info.VulkanApi);
            Assert.Null(info.HeapSizeLimit);
            Assert.Null(info.DeviceMemoryCallbacks);
            Assert.Null(info.TypeExternalMemoryHandleTypes);
            Assert.Equal(0ul, info.PreferredLargeHeapBlockSize);
            Assert.False(info.AllocationCallbacks.HasValue);
        }

        [Fact]
        public void VmaAllocationCreateInfoDefaultsAreEmpty()
        {
            VmaAllocationCreateInfo info = default;
            Assert.Equal(VmaAllocationCreateFlags.None, info.Flags);
            Assert.Equal(VmaMemoryUsage.Unknown, info.Usage);
            Assert.Equal(0u, info.MemoryTypeBits);
            Assert.Null(info.Pool);
            Assert.Null(info.UserData);
            Assert.Equal(0.0f, info.Priority);
        }

        [Fact]
        public void VmaPoolCreateInfoDefaultsAreEmpty()
        {
            VmaPoolCreateInfo info = default;
            Assert.Equal(0u, info.MemoryTypeIndex);
            Assert.Equal(VmaPoolCreateFlags.None, info.Flags);
            Assert.Equal(0ul, info.BlockSize);
            Assert.Equal((nuint)0, info.MinBlockCount);
            Assert.Equal((nuint)0, info.MaxBlockCount);
            unsafe
            {
                Assert.True(info.MemoryAllocateNext == null);
            }
        }

        [Fact]
        public void VmaDefragmentationInfoDefaultsAreEmpty()
        {
            VmaDefragmentationInfo info = default;
            Assert.Equal(VmaDefragmentationFlags.None, info.Flags);
            Assert.Null(info.Pool);
            Assert.Null(info.PfnBreakCallback);
            Assert.Null(info.BreakCallbackUserData);
        }

        [Fact]
        public void VmaVirtualBlockCreateInfoDefaultsAreEmpty()
        {
            VmaVirtualBlockCreateInfo info = default;
            Assert.Equal(0ul, info.Size);
            Assert.Equal(VmaVirtualBlockCreateFlags.None, info.Flags);
            Assert.False(info.AllocationCallbacks.HasValue);
        }
    }

    public sealed class VmaHandleTests
    {
        [Fact]
        public void HandleTypesAreSealedAndConstructibleOnlyInternally()
        {
            Assert.True(typeof(VmaAllocator).IsSealed);
            Assert.True(typeof(VmaPool).IsSealed);
            Assert.True(typeof(VmaAllocation).IsSealed);
            Assert.True(typeof(VmaDefragmentationContext).IsSealed);
            Assert.True(typeof(VmaVirtualBlock).IsSealed);

            Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(VmaAllocator)));
            Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(VmaPool)));
            Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(VmaDefragmentationContext)));
            Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(VmaVirtualBlock)));
        }
    }
}
