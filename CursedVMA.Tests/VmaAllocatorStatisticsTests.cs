// Tests for Phase 11: allocator-wide statistics (GetStatistics,
// CalculateStatistics) and heap budgets (GetHeapBudgets).

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using System;
using Xunit;

namespace CursedVMA.Tests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Shared setup: two memory types on two heaps, large enough that normal
    // block-vector allocations can actually occur without OOM.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class VmaAllocatorGetStatisticsTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device         s_Device         = new Device((nint)1);

        private static (FakeVulkanFunctions vk, VmaAllocator allocator) MakeAllocator(
            ulong heap0Size = 8ul * 1024 * 1024 * 1024,
            ulong heap1Size = 4ul * 1024 * 1024 * 1024)
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
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.HostCoherentBit,
                HeapIndex = 1,
            };
            memProps.MemoryHeapCount = 2;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = heap0Size };
            memProps.MemoryHeaps.Element1 = new MemoryHeap { Size = heap1Size };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device         = s_Device,
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            return (vk, allocator!);
        }

        // ── Empty allocator ───────────────────────────────────────────────────

        [Fact]
        public void GetStatistics_EmptyAllocator_AllZero()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            Span<VmaStatistics> stats = stackalloc VmaStatistics[2];
            allocator.GetStatistics(stats);

            Assert.Equal(0u, stats[0].BlockCount);
            Assert.Equal(0u, stats[1].BlockCount);
            Assert.Equal(0ul, stats[0].BlockBytes);
            Assert.Equal(0ul, stats[1].BlockBytes);
            Assert.Equal(0u,  stats[0].AllocationCount);
            Assert.Equal(0ul, stats[0].AllocationBytes);
        }

        [Fact]
        public void GetStatistics_SpanLargerThanTypeCount_ExtraEntriesZeroed()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            // Give a span of 4 even though only 2 types exist.
            Span<VmaStatistics> stats = stackalloc VmaStatistics[4];
            // Pre-fill with garbage to verify clear.
            for (int i = 0; i < 4; i++)
            {
                stats[i].BlockCount      = 99u;
                stats[i].AllocationCount = 99u;
            }

            allocator.GetStatistics(stats);

            Assert.Equal(0u, stats[2].BlockCount);
            Assert.Equal(0u, stats[3].BlockCount);
        }

        // ── Block-vector allocations ──────────────────────────────────────────

        [Fact]
        public void GetStatistics_AfterAllocation_ReflectsTypeStats()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements
            {
                Size = 1024,
                Alignment = 1,
                MemoryTypeBits = 0b01u, // only type 0 (DeviceLocal)
            };
            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            using var ___ = new AllocationScope(allocator, alloc!);

            Span<VmaStatistics> stats = stackalloc VmaStatistics[2];
            allocator.GetStatistics(stats);

            // Type 0 should have one allocation.
            Assert.Equal(1u, stats[0].AllocationCount);
            Assert.Equal(1024ul, stats[0].AllocationBytes);
            // Type 1 should remain empty.
            Assert.Equal(0u, stats[1].AllocationCount);
        }

        [Fact]
        public void GetStatistics_AfterFree_AllocationCountDrops()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements { Size = 512, Alignment = 1, MemoryTypeBits = 0b01u };
            var ci  = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            allocator.FreeMemory(alloc);

            Span<VmaStatistics> stats = stackalloc VmaStatistics[2];
            allocator.GetStatistics(stats);

            Assert.Equal(0u, stats[0].AllocationCount);
        }

        // ── Dedicated allocations ────────────────────────────────────────────

        [Fact]
        public void GetStatistics_DedicatedAllocation_CountedInCorrectType()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements
            {
                Size = 4096,
                Alignment = 1,
                MemoryTypeBits = 0b01u, // type 0
            };
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            using var ___ = new AllocationScope(allocator, alloc!);

            Span<VmaStatistics> stats = stackalloc VmaStatistics[2];
            allocator.GetStatistics(stats);

            // Dedicated alloc counts as 1 block + 1 allocation.
            Assert.Equal(1u, stats[0].BlockCount);
            Assert.Equal(4096ul, stats[0].BlockBytes);
            Assert.Equal(1u, stats[0].AllocationCount);
            Assert.Equal(4096ul, stats[0].AllocationBytes);
            Assert.Equal(0u, stats[1].BlockCount);
        }

        // ── Pool allocations ─────────────────────────────────────────────────

        [Fact]
        public void GetStatistics_PoolAllocation_ReflectedInCorrectType()
        {
            var (vk, allocator) = MakeAllocator();
            using var __ = allocator;

            var poolInfo = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 1, // host-visible type
                BlockSize       = 65536,
                MinBlockCount   = 1,
            };
            allocator.CreatePool(in poolInfo, out var pool);

            var req = new MemoryRequirements { Size = 256, Alignment = 1, MemoryTypeBits = 0b11u };
            var ci  = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.CpuOnly,
                Pool  = pool,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            Span<VmaStatistics> stats = stackalloc VmaStatistics[2];
            allocator.GetStatistics(stats);

            // The pool allocation should appear on type 1.
            Assert.Equal(1u, stats[1].AllocationCount);
            Assert.Equal(256ul, stats[1].AllocationBytes);

            allocator.FreeMemory(alloc);
            allocator.DestroyPool(pool!);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    public sealed class VmaAllocatorCalculateStatisticsTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device         s_Device         = new Device((nint)1);

        private static (FakeVulkanFunctions vk, VmaAllocator allocator) MakeAllocator()
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
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.HostCoherentBit,
                HeapIndex = 1,
            };
            memProps.MemoryHeapCount = 2;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = 8ul * 1024 * 1024 * 1024 };
            memProps.MemoryHeaps.Element1 = new MemoryHeap { Size = 4ul * 1024 * 1024 * 1024 };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device         = s_Device,
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            return (vk, allocator!);
        }

        // ── Empty allocator ───────────────────────────────────────────────────

        [Fact]
        public void CalculateStatistics_EmptyAllocator_AllZero()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            allocator.CalculateStatistics(out var total);

            Assert.Equal(0u, total.Total.Statistics.BlockCount);
            Assert.Equal(0u, total.Total.Statistics.AllocationCount);
            Assert.Equal(0ul, total.Total.Statistics.BlockBytes);
            Assert.Equal(0ul, total.Total.Statistics.AllocationBytes);
        }

        [Fact]
        public void CalculateStatistics_EmptyAllocator_PerTypeAllZero()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            allocator.CalculateStatistics(out var total);

            Assert.Equal(0u, total.MemoryType[0].Statistics.BlockCount);
            Assert.Equal(0u, total.MemoryType[1].Statistics.BlockCount);
            Assert.Equal(0u, total.MemoryHeap[0].Statistics.BlockCount);
            Assert.Equal(0u, total.MemoryHeap[1].Statistics.BlockCount);
        }

        // ── Single allocation ─────────────────────────────────────────────────

        [Fact]
        public void CalculateStatistics_OneAllocation_ReflectedInTypeHeapAndTotal()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements
            {
                Size = 2048,
                Alignment = 1,
                MemoryTypeBits = 0b01u, // type 0 → heap 0
            };
            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            using var ___ = new AllocationScope(allocator, alloc!);

            allocator.CalculateStatistics(out var total);

            Assert.Equal(1u,   total.MemoryType[0].Statistics.AllocationCount);
            Assert.Equal(2048ul, total.MemoryType[0].Statistics.AllocationBytes);
            Assert.Equal(1u,   total.MemoryHeap[0].Statistics.AllocationCount);
            Assert.Equal(2048ul, total.MemoryHeap[0].Statistics.AllocationBytes);
            Assert.Equal(1u,   total.Total.Statistics.AllocationCount);
            Assert.Equal(2048ul, total.Total.Statistics.AllocationBytes);

            // Type 1 and heap 1 must not be affected.
            Assert.Equal(0u, total.MemoryType[1].Statistics.AllocationCount);
            Assert.Equal(0u, total.MemoryHeap[1].Statistics.AllocationCount);
        }

        // ── AllocationSizeMin / Max ───────────────────────────────────────────

        [Fact]
        public void CalculateStatistics_MultipleAllocations_SizeExtrema()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };

            var req1 = new MemoryRequirements { Size = 100,  Alignment = 1, MemoryTypeBits = 0b01u };
            var req2 = new MemoryRequirements { Size = 4000, Alignment = 1, MemoryTypeBits = 0b01u };
            allocator.AllocateMemory(in req1, in ci, out var a1);
            allocator.AllocateMemory(in req2, in ci, out var a2);
            using var _1 = new AllocationScope(allocator, a1!);
            using var _2 = new AllocationScope(allocator, a2!);

            allocator.CalculateStatistics(out var total);

            Assert.Equal(100ul,  total.Total.AllocationSizeMin);
            Assert.Equal(4000ul, total.Total.AllocationSizeMax);
        }

        // ── Dedicated allocations ────────────────────────────────────────────

        [Fact]
        public void CalculateStatistics_DedicatedAllocation_CountedAsBlockAndAllocation()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements
            {
                Size = 8192,
                Alignment = 1,
                MemoryTypeBits = 0b01u,
            };
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            using var ___ = new AllocationScope(allocator, alloc!);

            allocator.CalculateStatistics(out var total);

            // Dedicated allocation: 1 block of 8192 + 1 allocation of 8192.
            Assert.Equal(1u,    total.Total.Statistics.BlockCount);
            Assert.Equal(8192ul, total.Total.Statistics.BlockBytes);
            Assert.Equal(1u,    total.Total.Statistics.AllocationCount);
            Assert.Equal(8192ul, total.Total.Statistics.AllocationBytes);
            Assert.Equal(8192ul, total.Total.AllocationSizeMin);
            Assert.Equal(8192ul, total.Total.AllocationSizeMax);
        }

        // ── Two types, same heap ──────────────────────────────────────────────

        [Fact]
        public void CalculateStatistics_TwoTypesOneHeap_HeapSumsTypes()
        {
            // Create a 3-type allocator where types 0 and 1 share heap 0.
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 3;
            memProps.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                HeapIndex = 0,
            };
            memProps.MemoryTypes.Element1 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit
                              | MemoryPropertyFlags.HostVisibleBit,
                HeapIndex = 0, // same heap!
            };
            memProps.MemoryTypes.Element2 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.HostCoherentBit,
                HeapIndex = 1,
            };
            memProps.MemoryHeapCount = 2;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = 8ul * 1024 * 1024 * 1024 };
            memProps.MemoryHeaps.Element1 = new MemoryHeap { Size = 4ul * 1024 * 1024 * 1024 };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice((nint)1),
                Device         = new Device((nint)1),
            };
            VmaAllocator.Create(vk, in info, out var allocatorNullable);
            VmaAllocator allocator = allocatorNullable!;
            using var __ = allocator;

            // Allocate on type 0 and type 1 separately (both on heap 0).
            var ci0 = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                MemoryTypeBits = 0b001u, // only type 0
            };
            var req0 = new MemoryRequirements { Size = 512, Alignment = 1, MemoryTypeBits = 0b001u };
            allocator.AllocateMemory(in req0, in ci0, out var a0);
            using var _0 = new AllocationScope(allocator, a0!);

            var ci1 = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                MemoryTypeBits = 0b010u, // only type 1
            };
            var req1 = new MemoryRequirements { Size = 768, Alignment = 1, MemoryTypeBits = 0b010u };
            allocator.AllocateMemory(in req1, in ci1, out var a1);
            using var _1 = new AllocationScope(allocator, a1!);

            allocator.CalculateStatistics(out var total);

            // Heap 0 should aggregate from both types.
            Assert.Equal(2u,    total.MemoryHeap[0].Statistics.AllocationCount);
            Assert.Equal(1u,    total.MemoryType[0].Statistics.AllocationCount);
            Assert.Equal(1u,    total.MemoryType[1].Statistics.AllocationCount);
            Assert.Equal(0u,    total.MemoryHeap[1].Statistics.AllocationCount);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    public sealed class VmaAllocatorGetHeapBudgetsTests
    {
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device         s_Device         = new Device((nint)1);

        private const ulong Heap0Size = 8ul * 1024 * 1024 * 1024;
        private const ulong Heap1Size = 4ul * 1024 * 1024 * 1024;

        private static (FakeVulkanFunctions vk, VmaAllocator allocator) MakeAllocator()
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
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.HostCoherentBit,
                HeapIndex = 1,
            };
            memProps.MemoryHeapCount = 2;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = Heap0Size };
            memProps.MemoryHeaps.Element1 = new MemoryHeap { Size = Heap1Size };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device         = s_Device,
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            return (vk, allocator!);
        }

        // ── Empty allocator ───────────────────────────────────────────────────

        [Fact]
        public void GetHeapBudgets_EmptyAllocator_UsageIsZero()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            Span<VmaBudget> budgets = stackalloc VmaBudget[2];
            allocator.GetHeapBudgets(budgets);

            Assert.Equal(0ul, budgets[0].Usage);
            Assert.Equal(0ul, budgets[1].Usage);
            Assert.Equal(0u,  budgets[0].Statistics.BlockCount);
        }

        [Fact]
        public void GetHeapBudgets_EmptyAllocator_BudgetIs80PercentOfHeapSize()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            Span<VmaBudget> budgets = stackalloc VmaBudget[2];
            allocator.GetHeapBudgets(budgets);

            Assert.Equal(Heap0Size * 8 / 10, budgets[0].Budget);
            Assert.Equal(Heap1Size * 8 / 10, budgets[1].Budget);
        }

        // ── After allocation ──────────────────────────────────────────────────

        [Fact]
        public void GetHeapBudgets_AfterAllocation_UsageEqualsBlockBytes()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements
            {
                Size = 65536,
                Alignment = 1,
                MemoryTypeBits = 0b01u, // type 0 → heap 0
            };
            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            using var ___ = new AllocationScope(allocator, alloc!);

            Span<VmaBudget> budgets = stackalloc VmaBudget[2];
            allocator.GetHeapBudgets(budgets);

            // Usage = BlockBytes (block may be larger than the allocation).
            Assert.Equal(budgets[0].Statistics.BlockBytes, budgets[0].Usage);
            Assert.True(budgets[0].Usage > 0);
            // Heap 1 is unaffected.
            Assert.Equal(0ul, budgets[1].Usage);
        }

        [Fact]
        public void GetHeapBudgets_DedicatedAllocation_UsageEqualsAllocSize()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            const ulong AllocSize = 32768ul;
            var req = new MemoryRequirements
            {
                Size = AllocSize,
                Alignment = 1,
                MemoryTypeBits = 0b01u,
            };
            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            using var ___ = new AllocationScope(allocator, alloc!);

            Span<VmaBudget> budgets = stackalloc VmaBudget[2];
            allocator.GetHeapBudgets(budgets);

            // Dedicated alloc: BlockBytes == AllocSize.
            Assert.Equal(AllocSize, budgets[0].Usage);
            Assert.Equal(AllocSize, budgets[0].Statistics.BlockBytes);
        }

        [Fact]
        public void GetHeapBudgets_BudgetNeverExceedsHeapSize()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            Span<VmaBudget> budgets = stackalloc VmaBudget[2];
            allocator.GetHeapBudgets(budgets);

            Assert.True(budgets[0].Budget <= Heap0Size);
            Assert.True(budgets[1].Budget <= Heap1Size);
        }

        [Fact]
        public void GetHeapBudgets_SpanLargerThanHeapCount_ExtraEntriesZeroed()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            Span<VmaBudget> budgets = stackalloc VmaBudget[4];
            for (int i = 0; i < 4; i++)
            {
                budgets[i].Budget = 999ul;
                budgets[i].Usage  = 999ul;
            }

            allocator.GetHeapBudgets(budgets);

            // Only first 2 heaps filled; extra entries cleared.
            Assert.Equal(0ul, budgets[2].Budget);
            Assert.Equal(0ul, budgets[3].Budget);
            Assert.Equal(0ul, budgets[2].Usage);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    public sealed class VmaMergeDetailedStatisticsTests
    {
        [Fact]
        public void MergeDetailedStatistics_TwoEmptySources_RemainsZero()
        {
            VmaDetailedStatistics dst = default;
            VmaDetailedStatistics src = default;

            VmaStatisticsHelper.MergeDetailedStatistics(ref dst, in src);

            Assert.Equal(0u,  dst.Statistics.AllocationCount);
            Assert.Equal(0ul, dst.AllocationSizeMin);
            Assert.Equal(0ul, dst.AllocationSizeMax);
        }

        [Fact]
        public void MergeDetailedStatistics_DstEmpty_TakesSrcValues()
        {
            VmaDetailedStatistics dst = default;
            VmaDetailedStatistics src = default;
            VmaStatisticsHelper.AddDetailedStatisticsAllocation(ref src, 200ul);

            VmaStatisticsHelper.MergeDetailedStatistics(ref dst, in src);

            Assert.Equal(1u,   dst.Statistics.AllocationCount);
            Assert.Equal(200ul, dst.AllocationSizeMin);
            Assert.Equal(200ul, dst.AllocationSizeMax);
        }

        [Fact]
        public void MergeDetailedStatistics_BothHaveAllocations_TakesMin()
        {
            VmaDetailedStatistics dst = default;
            VmaStatisticsHelper.AddDetailedStatisticsAllocation(ref dst, 500ul);

            VmaDetailedStatistics src = default;
            VmaStatisticsHelper.AddDetailedStatisticsAllocation(ref src, 100ul);

            VmaStatisticsHelper.MergeDetailedStatistics(ref dst, in src);

            Assert.Equal(100ul, dst.AllocationSizeMin);
            Assert.Equal(500ul, dst.AllocationSizeMax);
            Assert.Equal(2u,    dst.Statistics.AllocationCount);
            Assert.Equal(600ul, dst.Statistics.AllocationBytes);
        }

        [Fact]
        public void MergeDetailedStatistics_UnusedRanges_MergedCorrectly()
        {
            VmaDetailedStatistics dst = default;
            VmaStatisticsHelper.AddDetailedStatisticsUnusedRange(ref dst, 300ul);

            VmaDetailedStatistics src = default;
            VmaStatisticsHelper.AddDetailedStatisticsUnusedRange(ref src, 50ul);

            VmaStatisticsHelper.MergeDetailedStatistics(ref dst, in src);

            Assert.Equal(2u,   dst.UnusedRangeCount);
            Assert.Equal(50ul, dst.UnusedRangeSizeMin);
            Assert.Equal(300ul, dst.UnusedRangeSizeMax);
        }

        [Fact]
        public void MergeStatistics_AddsAllFields()
        {
            VmaStatistics dst = new VmaStatistics
            {
                BlockCount = 2, AllocationCount = 3, BlockBytes = 1024, AllocationBytes = 512,
            };
            VmaStatistics src = new VmaStatistics
            {
                BlockCount = 1, AllocationCount = 2, BlockBytes = 2048, AllocationBytes = 256,
            };

            VmaStatisticsHelper.MergeStatistics(ref dst, in src);

            Assert.Equal(3u,    dst.BlockCount);
            Assert.Equal(5u,    dst.AllocationCount);
            Assert.Equal(3072ul, dst.BlockBytes);
            Assert.Equal(768ul,  dst.AllocationBytes);
        }
    }

    // Helper RAII wrapper for freeing allocations in tests.
    file sealed class AllocationScope : IDisposable
    {
        private readonly VmaAllocator m_Allocator;
        private readonly VmaAllocation m_Allocation;

        internal AllocationScope(VmaAllocator allocator, VmaAllocation allocation)
        {
            m_Allocator  = allocator;
            m_Allocation = allocation;
        }

        public void Dispose() => m_Allocator.FreeMemory(m_Allocation);
    }
}
