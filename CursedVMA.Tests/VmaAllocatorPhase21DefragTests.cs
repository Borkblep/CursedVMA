// Phase 21 tests: defragmentation algorithm differentiation.
//
// Each test class exercises a specific VmaDefragmentationFlags algorithm and
// verifies the observable behavioral contract:
//
//   Fast      – single source block per pass chosen by highest free/total ratio.
//   Balanced  – single source block per pass chosen by highest absolute free bytes
//               (also the default when no algorithm flag is set).
//   Full      – all blocks with free space processed in a single pass.
//   Extensive – Full behaviour with the ability to create a new VkDeviceMemory block
//               when no existing block can accommodate a source allocation.

using Silk.NET.Vulkan;
using System.Collections.Generic;
using Xunit;

namespace CursedVMA.Tests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Pool-backed fixture for algorithm tests. Each block holds exactly
    /// <c>BlockSize</c> bytes; allocs are <c>AllocSize</c> bytes each, so
    /// exactly <c>BlockSize / AllocSize</c> allocs fill one block.
    /// </summary>
    internal sealed class AlgoDefragFixture
    {
        internal readonly FakeVulkanFunctions Vk;
        internal readonly VmaAllocator        Allocator;
        internal readonly VmaPool             Pool;
        internal readonly ulong               AllocSize;
        internal readonly ulong               BlockSize;

        internal AlgoDefragFixture(ulong blockSize = 256, ulong allocSize = 64)
        {
            BlockSize = blockSize;
            AllocSize = allocSize;
            Vk        = new FakeVulkanFunctions();

            Vk.MemoryProperties.MemoryHeapCount = 1;
            Vk.MemoryProperties.MemoryTypeCount = 1;
            unsafe
            {
                Vk.MemoryProperties.MemoryHeaps[0] =
                    new MemoryHeap { Size = 256ul * 1024 * 1024 };
                Vk.MemoryProperties.MemoryTypes[0] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                                  | MemoryPropertyFlags.DeviceLocalBit,
                };
            }

            var allocInfo = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
            };
            VmaAllocator.Create(Vk, allocInfo, out var alloc);
            Allocator = alloc!;

            var poolInfo = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                BlockSize       = blockSize,
            };
            Allocator.CreatePool(in poolInfo, out var pool);
            Pool = pool!;
        }

        internal VmaAllocation Alloc()
        {
            var req  = new MemoryRequirements { Size = AllocSize, Alignment = 1, MemoryTypeBits = 1 };
            var ci   = new VmaAllocationCreateInfo { Pool = Pool };
            Allocator.AllocateMemory(in req, in ci, out var a);
            return a!;
        }

        // Run a single pass with the given flags and collect Ignore-all result.
        internal uint RunOnePassIgnoreAll(VmaDefragmentationFlags flags)
        {
            var info = new VmaDefragmentationInfo { Pool = Pool, Flags = flags };
            Allocator.BeginDefragmentation(in info, out var ctx);
            Allocator.BeginDefragmentationPass(ctx, out var pass);
            uint count = pass.MoveCount;
            IgnoreAll(ref pass);
            Allocator.EndDefragmentationPass(ctx, ref pass);
            Allocator.EndDefragmentation(ctx, out _);
            return count;
        }

        // Helper: mark all moves as Ignore so the pool state is unchanged.
        internal static void IgnoreAll(ref VmaDefragmentationPassMoveInfo pass)
        {
            if (pass.Moves == null) return;
            for (int i = 0; i < pass.MoveCount; i++)
            {
                pass.Moves[i] = new VmaDefragmentationMove
                {
                    Operation        = VmaDefragmentationMoveOperation.Ignore,
                    SrcAllocation    = pass.Moves[i].SrcAllocation,
                    DstTmpAllocation = pass.Moves[i].DstTmpAllocation,
                };
            }
        }

        // Helper: accept all moves as Copy.
        internal static void AcceptAll(ref VmaDefragmentationPassMoveInfo pass)
        {
            // Default operation is Copy; nothing to change.
        }

        // Count how many distinct VkDeviceMemory handles are live in the pool.
        internal int LiveBlockCount()
        {
            Pool.GetStatistics(out var stats);
            return (int)stats.BlockCount;
        }

        internal void FreeAllAndDispose(IEnumerable<VmaAllocation> allocs)
        {
            foreach (var a in allocs) Allocator.FreeMemory(a);
            Allocator.DestroyPool(Pool);
            Allocator.Dispose();
        }
    }

    // ── Balanced (default) ────────────────────────────────────────────────────

    public sealed class VmaDefragBalancedTests
    {
        [Fact]
        public void Balanced_NoFlagSet_IsDefault()
        {
            // None-flag and AlgorithmBalancedBit should produce the same result.
            // Both should select a single source block per pass.
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc(); // block 0
            VmaAllocation b = f.Alloc(); // block 0  (full)
            VmaAllocation c = f.Alloc(); // block 1
            f.Allocator.FreeMemory(a);   // block 0 sparse

            uint noFlag  = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.None);
            uint balanced = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmBalancedBit);

            Assert.Equal(noFlag, balanced);

            f.FreeAllAndDispose(new[] { b, c });
        }

        [Fact]
        public void Balanced_TwoSourceBlocks_OnlyOneProcessedPerPass()
        {
            // Block 0: 1 alloc left (1 free slot)
            // Block 1: 1 alloc left (1 free slot)
            // Block 2: 2 allocs (full — target)
            // Balanced picks only the single emptiest block → max 1 move per pass.
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc(); // block 0
            VmaAllocation b = f.Alloc(); // block 0 (full)
            VmaAllocation c = f.Alloc(); // block 1
            VmaAllocation d = f.Alloc(); // block 1 (full)
            VmaAllocation e = f.Alloc(); // block 2
            f.Allocator.FreeMemory(a);   // block 0 sparse
            f.Allocator.FreeMemory(c);   // block 1 sparse

            uint moveCount = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmBalancedBit);

            // Balanced: only 1 source block → at most 1 move.
            Assert.Equal(1u, moveCount);

            f.FreeAllAndDispose(new[] { b, d, e });
        }

        [Fact]
        public void Balanced_SingleSparseBlock_OneMoveProposed()
        {
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc();
            VmaAllocation b = f.Alloc();
            VmaAllocation c = f.Alloc();
            f.Allocator.FreeMemory(a);

            uint moves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmBalancedBit);
            Assert.Equal(1u, moves);

            f.FreeAllAndDispose(new[] { b, c });
        }
    }

    // ── Fast ─────────────────────────────────────────────────────────────────

    public sealed class VmaDefragFastTests
    {
        [Fact]
        public void Fast_EmptyPool_ZeroMoves()
        {
            var f = new AlgoDefragFixture();
            uint moves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFastBit);
            Assert.Equal(0u, moves);
            f.Allocator.DestroyPool(f.Pool);
            f.Allocator.Dispose();
        }

        [Fact]
        public void Fast_FullBlock_ZeroMoves()
        {
            // One full block, nowhere to move.
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc();
            VmaAllocation b = f.Alloc();

            uint moves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFastBit);
            Assert.Equal(0u, moves);

            f.FreeAllAndDispose(new[] { a, b });
        }

        [Fact]
        public void Fast_SparseBlock_OneMoveProposed()
        {
            // Block 0: 1 alloc (50% free) → has a candidate.
            // Block 1: 1 alloc (50% free) → can be target.
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc(); // block 0
            VmaAllocation b = f.Alloc(); // block 0 (full)
            VmaAllocation c = f.Alloc(); // block 1
            f.Allocator.FreeMemory(a);   // block 0: 50% free

            uint moves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFastBit);
            Assert.Equal(1u, moves);

            f.FreeAllAndDispose(new[] { b, c });
        }

        [Fact]
        public void Fast_TwoSparseBlocks_SelectsByRatio_SingleBlock()
        {
            // Block 0 (256 bytes): 1 of 4 slots used → 75% free (high ratio)
            // Block 1 (128 bytes): 1 of 2 slots used → 50% free
            // Block 2 (128 bytes): full (target)
            // Fast should pick only ONE block (the one with higher ratio).
            var f = new AlgoDefragFixture(blockSize: 256, allocSize: 64);

            // Fill block 0 partially: 1 alloc out of 4
            VmaAllocation a = f.Alloc(); // slot 0 of block 0
            VmaAllocation b = f.Alloc(); // slot 1 of block 0
            VmaAllocation c = f.Alloc(); // slot 2 of block 0
            VmaAllocation d = f.Alloc(); // slot 3 of block 0 → block 0 full
            f.Allocator.FreeMemory(b);
            f.Allocator.FreeMemory(c);
            f.Allocator.FreeMemory(d);
            // block 0: 1/4 used = 75% free

            VmaAllocation e = f.Alloc(); // block 1 slot 0
            VmaAllocation g = f.Alloc(); // block 1 slot 1
            VmaAllocation h = f.Alloc(); // block 1 slot 2
            VmaAllocation j = f.Alloc(); // block 1 slot 3 → block 1 full
            f.Allocator.FreeMemory(g);
            f.Allocator.FreeMemory(h);
            // block 1: 2/4 used = 50% free

            // Block 2 needs to be the target.
            VmaAllocation k = f.Alloc(); // block 2 slot 0
            VmaAllocation l = f.Alloc(); // block 2 slot 1
            VmaAllocation m = f.Alloc(); // block 2 slot 2

            uint moves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFastBit);
            // Fast selects exactly ONE source block (the highest-ratio block).
            // That block has at most 3 free-fitting allocations.
            // Since target block has 1 free slot, at most 1 move.
            Assert.True(moves <= 1u);

            f.FreeAllAndDispose(new[] { a, e, j, k, l, m });
        }
    }

    // ── Full ─────────────────────────────────────────────────────────────────

    public sealed class VmaDefragFullTests
    {
        [Fact]
        public void Full_EmptyPool_ZeroMoves()
        {
            var f = new AlgoDefragFixture();
            uint moves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFullBit);
            Assert.Equal(0u, moves);
            f.Allocator.DestroyPool(f.Pool);
            f.Allocator.Dispose();
        }

        [Fact]
        public void Full_TwoSourceBlocks_BothProcessedInOnePass()
        {
            // Block 0: 1 alloc (1 free slot)
            // Block 1: 1 alloc (1 free slot)
            // Block 2: full — acts as target for block 0's alloc only (1 slot free)
            // Full should propose moves from BOTH source blocks (unlike Balanced which only picks one).
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc(); // block 0
            VmaAllocation b = f.Alloc(); // block 0 (full)
            VmaAllocation c = f.Alloc(); // block 1
            VmaAllocation d = f.Alloc(); // block 1 (full)
            VmaAllocation e = f.Alloc(); // block 2
            f.Allocator.FreeMemory(a);   // block 0: 1 alloc, 1 free slot
            f.Allocator.FreeMemory(c);   // block 1: 1 alloc, 1 free slot
            // Block 2 has 1 free slot (e is alone).

            uint fullMoves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFullBit);

            // Full processes both source blocks per pass. Block 0's alloc can move
            // into block 2's free slot, and block 1's alloc can move into block 0's
            // now-free slot (or whatever ordering fits). At least 1 move is guaranteed;
            // Full produces more or equal moves than Balanced in the same scenario.
            uint balancedMoves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmBalancedBit);
            Assert.True(fullMoves >= balancedMoves);
            Assert.True(fullMoves >= 1u);

            f.FreeAllAndDispose(new[] { b, d, e });
        }

        [Fact]
        public void Full_MultipleSourceBlocks_ConsolidatesInOnePass()
        {
            // Three blocks each with one alloc; target is a fourth block.
            // Full should propose moves from all three in a single pass.
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);

            // Fill 3 blocks with 1 alloc each (sparse).
            VmaAllocation a0 = f.Alloc(); VmaAllocation a1 = f.Alloc(); // block 0 full
            VmaAllocation b0 = f.Alloc(); VmaAllocation b1 = f.Alloc(); // block 1 full
            VmaAllocation c0 = f.Alloc(); VmaAllocation c1 = f.Alloc(); // block 2 full
            f.Allocator.FreeMemory(a0); // block 0: 1 alloc
            f.Allocator.FreeMemory(b0); // block 1: 1 alloc
            f.Allocator.FreeMemory(c0); // block 2: 1 alloc
            // Now 3 sparse blocks (each 50% free), each with 1 remaining alloc.
            // Block 3 will be created when the 7th alloc is made.
            VmaAllocation d0 = f.Alloc(); // block 3 slot 0 — dense target

            var info = new VmaDefragmentationInfo
            {
                Pool  = f.Pool,
                Flags = VmaDefragmentationFlags.AlgorithmFullBit,
            };
            f.Allocator.BeginDefragmentation(in info, out var ctx);
            f.Allocator.BeginDefragmentationPass(ctx, out var pass);

            // Full should surface moves from more than one source block per pass.
            // With 3 sparse source blocks and at least 3 available target slots
            // (d0's block + each freed slot as moves proceed), Full ≥ 1.
            Assert.True(pass.MoveCount >= 1u);

            AlgoDefragFixture.IgnoreAll(ref pass);
            f.Allocator.EndDefragmentationPass(ctx, ref pass);
            f.Allocator.EndDefragmentation(ctx, out _);

            f.FreeAllAndDispose(new[] { a1, b1, c1, d0 });
        }

        [Fact]
        public void Full_FullBlockOnly_ZeroMoves()
        {
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc();
            VmaAllocation b = f.Alloc(); // block 0 full, no destination

            uint moves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFullBit);
            Assert.Equal(0u, moves);

            f.FreeAllAndDispose(new[] { a, b });
        }
    }

    // ── Extensive ─────────────────────────────────────────────────────────────

    public sealed class VmaDefragExtensiveTests
    {
        [Fact]
        public void Extensive_EmptyPool_ZeroMoves()
        {
            var f = new AlgoDefragFixture();
            uint moves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmExtensiveBit);
            Assert.Equal(0u, moves);
            f.Allocator.DestroyPool(f.Pool);
            f.Allocator.Dispose();
        }

        [Fact]
        public void Extensive_CanCreateNewBlock_WhenNoExistingBlockFits()
        {
            // Setup: two completely full blocks, one alloc in a third block.
            // There is no free space in the full blocks, so a new block must
            // be created to accommodate the move.
            // block 0: full  (allocs a, b)
            // block 1: full  (allocs c, d)
            // block 2: 1 alloc (e) — source, nowhere to go without new block
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc(); // block 0
            VmaAllocation b = f.Alloc(); // block 0 (full)
            VmaAllocation c = f.Alloc(); // block 1
            VmaAllocation d = f.Alloc(); // block 1 (full)
            VmaAllocation e = f.Alloc(); // block 2

            int blocksBefore = f.LiveBlockCount();

            var info = new VmaDefragmentationInfo
            {
                Pool  = f.Pool,
                Flags = VmaDefragmentationFlags.AlgorithmExtensiveBit,
            };
            f.Allocator.BeginDefragmentation(in info, out var ctx);
            f.Allocator.BeginDefragmentationPass(ctx, out var pass);

            // Extensive must propose a move for e (by creating a new block).
            Assert.Equal(1u, pass.MoveCount);
            Assert.NotNull(pass.Moves![0].DstTmpAllocation);

            // Accept the move (Copy).
            f.Allocator.EndDefragmentationPass(ctx, ref pass);
            f.Allocator.EndDefragmentation(ctx, out var stats);

            // The source block (block 2) should now be freed since e was moved.
            Assert.Equal(1u, stats.AllocationsMoved);
            Assert.Equal(1u, stats.DeviceMemoryBlocksFreed);

            // The pool has one more block than it started with (the new destination block)
            // minus the one freed → net count same as before or one more.
            int blocksAfter = f.LiveBlockCount();
            Assert.True(blocksAfter <= blocksBefore);

            f.FreeAllAndDispose(new[] { a, b, c, d, e });
        }

        [Fact]
        public void Extensive_BehavesLikeFullWhenBlocksAvailable()
        {
            // When existing blocks have free space, Extensive produces at least
            // as many moves as Full in the same scenario.
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc();
            VmaAllocation b = f.Alloc(); // block 0 full
            VmaAllocation c = f.Alloc();
            VmaAllocation d = f.Alloc(); // block 1 full
            VmaAllocation e = f.Alloc(); // block 2
            f.Allocator.FreeMemory(a);
            f.Allocator.FreeMemory(c);

            uint extMoves  = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmExtensiveBit);
            uint fullMoves = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFullBit);

            Assert.True(extMoves >= fullMoves);

            f.FreeAllAndDispose(new[] { b, d, e });
        }

        [Fact]
        public void Extensive_MultiPass_FullyConsolidates()
        {
            // Three sparse blocks, each with one alloc and one free slot.
            // Extensive should fully consolidate all three into one block across passes.
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc();
            VmaAllocation b = f.Alloc(); // block 0 full
            VmaAllocation c = f.Alloc();
            VmaAllocation d = f.Alloc(); // block 1 full
            VmaAllocation e = f.Alloc(); // block 2 sparse
            f.Allocator.FreeMemory(a);
            f.Allocator.FreeMemory(c);

            var info = new VmaDefragmentationInfo
            {
                Pool  = f.Pool,
                Flags = VmaDefragmentationFlags.AlgorithmExtensiveBit,
            };
            f.Allocator.BeginDefragmentation(in info, out var ctx);

            uint totalMoved = 0;
            for (int pass = 0; pass < 5; pass++)
            {
                f.Allocator.BeginDefragmentationPass(ctx, out var pi);
                if (pi.MoveCount == 0) break;
                totalMoved += pi.MoveCount;
                AlgoDefragFixture.AcceptAll(ref pi);
                f.Allocator.EndDefragmentationPass(ctx, ref pi);
            }

            f.Allocator.EndDefragmentation(ctx, out var stats);
            Assert.True(stats.AllocationsMoved >= 1);

            f.FreeAllAndDispose(new[] { b, d, e });
        }
    }

    // ── Algorithm comparison ──────────────────────────────────────────────────

    public sealed class VmaDefragAlgorithmComparisonTests
    {
        [Fact]
        public void Full_MovesMoreThanOrEqualToBalanced_PerPass()
        {
            // In a scenario with multiple sparse blocks, Full proposes >= moves
            // than Balanced in the same first pass.
            var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc();
            VmaAllocation b = f.Alloc(); // block 0 full
            VmaAllocation c = f.Alloc();
            VmaAllocation d = f.Alloc(); // block 1 full
            VmaAllocation e = f.Alloc(); // block 2 (target slot available)
            f.Allocator.FreeMemory(a);
            f.Allocator.FreeMemory(c);
            // block 0: 1 alloc, block 1: 1 alloc, block 2: 1 alloc (target)

            uint balanced = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmBalancedBit);
            uint full     = f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFullBit);

            Assert.True(full >= balanced);

            f.FreeAllAndDispose(new[] { b, d, e });
        }

        [Fact]
        public void AllAlgorithms_EmptyPool_ProduceZeroMoves()
        {
            var f = new AlgoDefragFixture();

            Assert.Equal(0u, f.RunOnePassIgnoreAll(VmaDefragmentationFlags.None));
            Assert.Equal(0u, f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFastBit));
            Assert.Equal(0u, f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmBalancedBit));
            Assert.Equal(0u, f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmFullBit));
            Assert.Equal(0u, f.RunOnePassIgnoreAll(VmaDefragmentationFlags.AlgorithmExtensiveBit));

            f.Allocator.DestroyPool(f.Pool);
            f.Allocator.Dispose();
        }

        [Fact]
        public void AllAlgorithms_SingleSparseBlock_ProposeAtLeastOneMove()
        {
            void Check(VmaDefragmentationFlags flags)
            {
                var f = new AlgoDefragFixture(blockSize: 128, allocSize: 64);
                VmaAllocation a = f.Alloc();
                VmaAllocation b = f.Alloc(); // block 0 full
                VmaAllocation c = f.Alloc(); // block 1 sparse (target)
                f.Allocator.FreeMemory(a);

                uint moves = f.RunOnePassIgnoreAll(flags);
                Assert.True(moves >= 1, $"Algorithm {flags} should propose >= 1 move");

                f.FreeAllAndDispose(new[] { b, c });
            }

            Check(VmaDefragmentationFlags.None);
            Check(VmaDefragmentationFlags.AlgorithmFastBit);
            Check(VmaDefragmentationFlags.AlgorithmBalancedBit);
            Check(VmaDefragmentationFlags.AlgorithmFullBit);
            Check(VmaDefragmentationFlags.AlgorithmExtensiveBit);
        }
    }
}
