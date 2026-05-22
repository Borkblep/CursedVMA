// Tests for VmaAllocator defragmentation API:
//   BeginDefragmentation / BeginDefragmentationPass /
//   EndDefragmentationPass / EndDefragmentation.
//
// All tests use a VmaPool with an explicit small BlockSize so we control
// exactly when new blocks are created and can construct reproducible
// two-block scenarios.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using System;
using Xunit;

namespace CursedVMA.Tests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    internal sealed class DefragFixture : IDisposable
    {
        internal readonly FakeVulkanFunctions Vk;
        internal readonly VmaAllocator        Allocator;
        internal readonly VmaPool             Pool;

        // blockSize: bytes per block (pool uses explicit block size).
        // allocSize: bytes per test allocation (alignment always 1).
        internal readonly ulong AllocSize;

        internal DefragFixture(ulong blockSize = 128, ulong allocSize = 64)
        {
            AllocSize = allocSize;
            Vk        = new FakeVulkanFunctions();

            // Single heap / single memory type (HostVisible + DeviceLocal).
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

            var allocatorInfo = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
            };
            Result r = VmaAllocator.Create(Vk, allocatorInfo, out VmaAllocator? alloc);
            Assert.Equal(Result.Success, r);
            Allocator = alloc!;

            var poolInfo = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                BlockSize       = blockSize,
            };
            r = Allocator.CreatePool(in poolInfo, out VmaPool? pool);
            Assert.Equal(Result.Success, r);
            Pool = pool!;
        }

        internal VmaAllocation Alloc()
        {
            var req  = new MemoryRequirements { Size = AllocSize, Alignment = 1, MemoryTypeBits = 1 };
            var info = new VmaAllocationCreateInfo { Pool = Pool };
            Result r = Allocator.AllocateMemory(in req, in info, out VmaAllocation? a);
            Assert.Equal(Result.Success, r);
            return a!;
        }

        public void Dispose()
        {
            Allocator.DestroyPool(Pool);
            Allocator.Dispose();
        }
    }

    // ── test classes ─────────────────────────────────────────────────────────

    public sealed class VmaDefragmentationBeginTests
    {
        [Fact]
        public void BeginDefragmentation_ReturnsSuccess()
        {
            using var f = new DefragFixture();
            Result r = f.Allocator.BeginDefragmentation(
                new VmaDefragmentationInfo { Pool = f.Pool }, out var ctx);
            Assert.Equal(Result.Success, r);
            f.Allocator.EndDefragmentation(ctx, out _);
        }

        [Fact]
        public void BeginDefragmentation_EmptyPool_PassProducesZeroMoves()
        {
            using var f = new DefragFixture();
            f.Allocator.BeginDefragmentation(
                new VmaDefragmentationInfo { Pool = f.Pool }, out var ctx);

            f.Allocator.BeginDefragmentationPass(ctx, out var pass);

            Assert.Equal(0u, pass.MoveCount);
            f.Allocator.EndDefragmentation(ctx, out var stats);
            Assert.Equal(0u, stats.AllocationsMoved);
            Assert.Equal(0u, stats.DeviceMemoryBlocksFreed);
        }

        [Fact]
        public void BeginDefragmentation_SingleFullBlock_ZeroMoves()
        {
            // Block size = 128, alloc size = 64 → 2 allocs fill the block.
            // No second block exists, so nothing can be moved.
            using var f = new DefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc();
            VmaAllocation b = f.Alloc();

            f.Allocator.BeginDefragmentation(
                new VmaDefragmentationInfo { Pool = f.Pool }, out var ctx);
            f.Allocator.BeginDefragmentationPass(ctx, out var pass);

            Assert.Equal(0u, pass.MoveCount);

            f.Allocator.EndDefragmentation(ctx, out _);
            f.Allocator.FreeMemory(a);
            f.Allocator.FreeMemory(b);
        }

        [Fact]
        public void BeginDefragmentation_TwoBlocks_OneSparse_OneMoveProposed()
        {
            using var f = new DefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc(); // block 0 [0-64)
            VmaAllocation b = f.Alloc(); // block 0 [64-128) — block 0 full
            VmaAllocation c = f.Alloc(); // block 1 [0-64)
            f.Allocator.FreeMemory(a);   // block 0: 1 alloc, 64 bytes free

            f.Allocator.BeginDefragmentation(
                new VmaDefragmentationInfo { Pool = f.Pool }, out var ctx);
            f.Allocator.BeginDefragmentationPass(ctx, out var pass);

            Assert.Equal(1u, pass.MoveCount);
            Assert.NotNull(pass.Moves![0].SrcAllocation);
            Assert.NotNull(pass.Moves[0].DstTmpAllocation);
            Assert.Equal(VmaDefragmentationMoveOperation.Copy, pass.Moves[0].Operation);

            // Ignore this pass and clean up.
            pass.Moves[0] = new VmaDefragmentationMove
            {
                Operation        = VmaDefragmentationMoveOperation.Ignore,
                SrcAllocation    = pass.Moves[0].SrcAllocation,
                DstTmpAllocation = pass.Moves[0].DstTmpAllocation,
            };
            f.Allocator.EndDefragmentationPass(ctx, ref pass);
            f.Allocator.EndDefragmentation(ctx, out _);

            f.Allocator.FreeMemory(b);
            f.Allocator.FreeMemory(c);
        }
    }

    public sealed class VmaDefragmentationCopyTests
    {
        // Setup: block 0 has [a(freed), b], block 1 has [c].
        private static (DefragFixture f, VmaAllocation b, VmaAllocation c,
                         VmaDefragmentationContext ctx, VmaDefragmentationPassMoveInfo pass)
            TwoBlockSetup()
        {
            var f = new DefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc(); // block 0 [0-64)
            VmaAllocation b = f.Alloc(); // block 0 [64-128)
            VmaAllocation c = f.Alloc(); // block 1 [0-64)
            f.Allocator.FreeMemory(a);

            f.Allocator.BeginDefragmentation(
                new VmaDefragmentationInfo { Pool = f.Pool }, out var ctx);
            f.Allocator.BeginDefragmentationPass(ctx, out var pass);
            return (f, b, c, ctx, pass);
        }

        [Fact]
        public void EndDefragmentationPass_CopyOperation_SrcMigratedToSameBlockAsC()
        {
            var (f, b, c, ctx, pass) = TwoBlockSetup();
            using (f)
            {
                // Operation is already Copy; apply.
                Result r = f.Allocator.EndDefragmentationPass(ctx, ref pass);
                Assert.Equal(Result.Success, r);

                // b should now share the same VkDeviceMemory as c.
                Assert.Equal(c.Memory.Handle, b.Memory.Handle);

                f.Allocator.EndDefragmentation(ctx, out _);
                f.Allocator.FreeMemory(b);
                f.Allocator.FreeMemory(c);
            }
        }

        [Fact]
        public void EndDefragmentationPass_CopyOperation_BlocksFreedCountIsOne()
        {
            var (f, b, c, ctx, pass) = TwoBlockSetup();
            using (f)
            {
                f.Allocator.EndDefragmentationPass(ctx, ref pass);
                f.Allocator.EndDefragmentation(ctx, out var stats);

                Assert.Equal(1u, stats.AllocationsMoved);
                Assert.Equal(1u, stats.DeviceMemoryBlocksFreed);

                f.Allocator.FreeMemory(b);
                f.Allocator.FreeMemory(c);
            }
        }

        [Fact]
        public void EndDefragmentationPass_CopyOperation_BytesMovedAndFreedCorrect()
        {
            var (f, b, c, ctx, pass) = TwoBlockSetup();
            using (f)
            {
                f.Allocator.EndDefragmentationPass(ctx, ref pass);
                f.Allocator.EndDefragmentation(ctx, out var stats);

                Assert.Equal(64ul, stats.BytesMoved);   // one 64-byte alloc moved
                Assert.Equal(128ul, stats.BytesFreed);  // one 128-byte block freed

                f.Allocator.FreeMemory(b);
                f.Allocator.FreeMemory(c);
            }
        }

        [Fact]
        public void EndDefragmentationPass_IgnoreOperation_SrcUnchanged()
        {
            var (f, b, c, ctx, pass) = TwoBlockSetup();
            using (f)
            {
                ulong bOriginalMemory = b.Memory.Handle;

                // Caller cannot perform the move this pass.
                pass.Moves![0] = new VmaDefragmentationMove
                {
                    Operation        = VmaDefragmentationMoveOperation.Ignore,
                    SrcAllocation    = pass.Moves[0].SrcAllocation,
                    DstTmpAllocation = pass.Moves[0].DstTmpAllocation,
                };
                f.Allocator.EndDefragmentationPass(ctx, ref pass);

                // b must still be in its original block.
                Assert.Equal(bOriginalMemory, b.Memory.Handle);

                f.Allocator.EndDefragmentation(ctx, out var stats);
                Assert.Equal(0u, stats.AllocationsMoved);

                f.Allocator.FreeMemory(b);
                f.Allocator.FreeMemory(c);
            }
        }

        [Fact]
        public void EndDefragmentationPass_IgnoreOperation_PoolStillHasTwoBlocks()
        {
            var (f, b, c, ctx, pass) = TwoBlockSetup();
            using (f)
            {
                pass.Moves![0] = new VmaDefragmentationMove
                {
                    Operation        = VmaDefragmentationMoveOperation.Ignore,
                    SrcAllocation    = pass.Moves[0].SrcAllocation,
                    DstTmpAllocation = pass.Moves[0].DstTmpAllocation,
                };
                f.Allocator.EndDefragmentationPass(ctx, ref pass);
                f.Allocator.EndDefragmentation(ctx, out _);

                f.Pool.GetStatistics(out var pStats);
                Assert.Equal(2u, pStats.BlockCount);

                f.Allocator.FreeMemory(b);
                f.Allocator.FreeMemory(c);
            }
        }

        [Fact]
        public void EndDefragmentationPass_DestroyOperation_SrcSlotFreed()
        {
            var (f, b, c, ctx, pass) = TwoBlockSetup();
            using (f)
            {
                // Caller destroys the allocation; VMA frees the metadata slot.
                pass.Moves![0] = new VmaDefragmentationMove
                {
                    Operation        = VmaDefragmentationMoveOperation.Destroy,
                    SrcAllocation    = pass.Moves[0].SrcAllocation,
                    DstTmpAllocation = pass.Moves[0].DstTmpAllocation,
                };
                f.Allocator.EndDefragmentationPass(ctx, ref pass);

                f.Allocator.EndDefragmentation(ctx, out var stats);
                // Destroy does not increment AllocationsMoved.
                Assert.Equal(0u, stats.AllocationsMoved);
                // The src's block should be empty and freed.
                Assert.Equal(1u, stats.DeviceMemoryBlocksFreed);

                // Only c remains alive; do not free b (its slot is already freed).
                f.Allocator.FreeMemory(c);
            }
        }
    }

    public sealed class VmaDefragmentationLimitTests
    {
        // Setup: block 0 has b, block 1 has c, and c's block also has room.
        private static (DefragFixture f, VmaAllocation b, VmaAllocation c)
            TwoAllocTwoBlockSetup()
        {
            var f = new DefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc(); // block 0 [0-64)
            VmaAllocation b = f.Alloc(); // block 0 [64-128)
            VmaAllocation c = f.Alloc(); // block 1 [0-64)
            f.Allocator.FreeMemory(a);
            return (f, b, c);
        }

        [Fact]
        public void MaxAllocationsPerPass_One_AtMostOneMove()
        {
            var (f, b, c) = TwoAllocTwoBlockSetup();
            using (f)
            {
                var info = new VmaDefragmentationInfo
                {
                    Pool                 = f.Pool,
                    MaxAllocationsPerPass = 1,
                };
                f.Allocator.BeginDefragmentation(in info, out var ctx);
                f.Allocator.BeginDefragmentationPass(ctx, out var pass);

                Assert.True(pass.MoveCount <= 1);

                // Ignore the move so we don't need to validate memory state.
                if (pass.MoveCount > 0)
                {
                    pass.Moves![0] = new VmaDefragmentationMove
                    {
                        Operation        = VmaDefragmentationMoveOperation.Ignore,
                        SrcAllocation    = pass.Moves[0].SrcAllocation,
                        DstTmpAllocation = pass.Moves[0].DstTmpAllocation,
                    };
                }
                f.Allocator.EndDefragmentationPass(ctx, ref pass);
                f.Allocator.EndDefragmentation(ctx, out _);

                f.Allocator.FreeMemory(b);
                f.Allocator.FreeMemory(c);
            }
        }

        [Fact]
        public void MaxBytesPerPass_TooSmall_ZeroMoves()
        {
            // AllocSize = 64; cap is smaller, so no move fits.
            var (f, b, c) = TwoAllocTwoBlockSetup();
            using (f)
            {
                var info = new VmaDefragmentationInfo
                {
                    Pool             = f.Pool,
                    MaxBytesPerPass  = 1,   // less than alloc size of 64
                };
                f.Allocator.BeginDefragmentation(in info, out var ctx);
                f.Allocator.BeginDefragmentationPass(ctx, out var pass);

                Assert.Equal(0u, pass.MoveCount);

                f.Allocator.EndDefragmentationPass(ctx, ref pass);
                f.Allocator.EndDefragmentation(ctx, out _);

                f.Allocator.FreeMemory(b);
                f.Allocator.FreeMemory(c);
            }
        }

        [Fact]
        public void BreakCallback_AlwaysTrue_ZeroMoves()
        {
            var (f, b, c) = TwoAllocTwoBlockSetup();
            using (f)
            {
                var info = new VmaDefragmentationInfo
                {
                    Pool              = f.Pool,
                    PfnBreakCallback  = _ => true,   // stop immediately
                };
                f.Allocator.BeginDefragmentation(in info, out var ctx);
                f.Allocator.BeginDefragmentationPass(ctx, out var pass);

                Assert.Equal(0u, pass.MoveCount);

                f.Allocator.EndDefragmentationPass(ctx, ref pass);
                f.Allocator.EndDefragmentation(ctx, out _);

                f.Allocator.FreeMemory(b);
                f.Allocator.FreeMemory(c);
            }
        }

        [Fact]
        public void BreakCallback_NeverCalled_AllMoves()
        {
            // Callback always returns false → doesn't stop early → moves should be proposed.
            var (f, b, c) = TwoAllocTwoBlockSetup();
            using (f)
            {
                var info = new VmaDefragmentationInfo
                {
                    Pool             = f.Pool,
                    PfnBreakCallback = _ => false,
                };
                f.Allocator.BeginDefragmentation(in info, out var ctx);
                f.Allocator.BeginDefragmentationPass(ctx, out var pass);

                Assert.Equal(1u, pass.MoveCount);

                pass.Moves![0] = new VmaDefragmentationMove
                {
                    Operation        = VmaDefragmentationMoveOperation.Ignore,
                    SrcAllocation    = pass.Moves[0].SrcAllocation,
                    DstTmpAllocation = pass.Moves[0].DstTmpAllocation,
                };
                f.Allocator.EndDefragmentationPass(ctx, ref pass);
                f.Allocator.EndDefragmentation(ctx, out _);

                f.Allocator.FreeMemory(b);
                f.Allocator.FreeMemory(c);
            }
        }
    }

    public sealed class VmaDefragmentationStatsTests
    {
        [Fact]
        public void EndDefragmentation_NoMoves_AllStatsZero()
        {
            using var f = new DefragFixture();
            f.Allocator.BeginDefragmentation(
                new VmaDefragmentationInfo { Pool = f.Pool }, out var ctx);
            f.Allocator.BeginDefragmentationPass(ctx, out var pass);
            f.Allocator.EndDefragmentationPass(ctx, ref pass);
            f.Allocator.EndDefragmentation(ctx, out var stats);

            Assert.Equal(0ul, stats.BytesMoved);
            Assert.Equal(0ul, stats.BytesFreed);
            Assert.Equal(0u,  stats.AllocationsMoved);
            Assert.Equal(0u,  stats.DeviceMemoryBlocksFreed);
        }

        [Fact]
        public void EndDefragmentation_AfterSuccessfulCopy_StatsPopulated()
        {
            using var f = new DefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc();
            VmaAllocation b = f.Alloc();
            VmaAllocation c = f.Alloc();
            f.Allocator.FreeMemory(a);

            f.Allocator.BeginDefragmentation(
                new VmaDefragmentationInfo { Pool = f.Pool }, out var ctx);
            f.Allocator.BeginDefragmentationPass(ctx, out var pass);
            f.Allocator.EndDefragmentationPass(ctx, ref pass);  // Copy (default)
            f.Allocator.EndDefragmentation(ctx, out var stats);

            Assert.Equal(64ul, stats.BytesMoved);
            Assert.Equal(128ul, stats.BytesFreed);
            Assert.Equal(1u,   stats.AllocationsMoved);
            Assert.Equal(1u,   stats.DeviceMemoryBlocksFreed);

            f.Allocator.FreeMemory(b);
            f.Allocator.FreeMemory(c);
        }

        [Fact]
        public void EndDefragmentation_MultiPass_StatsAccumulate()
        {
            // Four allocations across three blocks, freed in a way that requires
            // two passes to fully compact.
            //   Block 0 (128): a, b → free a → sparse
            //   Block 1 (128): c, d → free c → sparse
            //   Pass 1: move b to block 1 → block 0 freed
            //   Pass 2: no more spare blocks → done
            using var f = new DefragFixture(blockSize: 128, allocSize: 64);
            VmaAllocation a = f.Alloc(); // block 0 [0-64)
            VmaAllocation b = f.Alloc(); // block 0 [64-128)
            VmaAllocation c = f.Alloc(); // block 1 [0-64)
            VmaAllocation d = f.Alloc(); // block 1 [64-128)
            f.Allocator.FreeMemory(a);   // block 0 sparse
            f.Allocator.FreeMemory(c);   // block 1 sparse

            f.Allocator.BeginDefragmentation(
                new VmaDefragmentationInfo { Pool = f.Pool }, out var ctx);

            // Pass 1
            f.Allocator.BeginDefragmentationPass(ctx, out var pass1);
            Assert.True(pass1.MoveCount > 0);
            f.Allocator.EndDefragmentationPass(ctx, ref pass1);  // Copy

            // Pass 2
            f.Allocator.BeginDefragmentationPass(ctx, out var pass2);
            // Either another move or done.
            f.Allocator.EndDefragmentationPass(ctx, ref pass2);

            f.Allocator.EndDefragmentation(ctx, out var stats);

            // At least one allocation was moved and one block freed.
            Assert.True(stats.AllocationsMoved >= 1);
            Assert.True(stats.DeviceMemoryBlocksFreed >= 1);

            f.Allocator.FreeMemory(b);
            f.Allocator.FreeMemory(d);
        }
    }

    public sealed class VmaDefragmentationNoPoolTests
    {
        [Fact]
        public void BeginDefragmentation_NullPool_UsesDefaultVectors()
        {
            var vk = new FakeVulkanFunctions();
            vk.MemoryProperties.MemoryHeapCount = 1;
            vk.MemoryProperties.MemoryTypeCount = 1;
            unsafe
            {
                vk.MemoryProperties.MemoryHeaps[0] =
                    new MemoryHeap { Size = 256ul * 1024 * 1024 };
                vk.MemoryProperties.MemoryTypes[0] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit,
                };
            }

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
            };
            VmaAllocator.Create(vk, info, out var allocator);

            // No allocations → empty default vectors → 0 moves.
            Result r = allocator!.BeginDefragmentation(
                new VmaDefragmentationInfo { Pool = null }, out var ctx);
            Assert.Equal(Result.Success, r);

            allocator.BeginDefragmentationPass(ctx, out var pass);
            Assert.Equal(0u, pass.MoveCount);

            allocator.EndDefragmentationPass(ctx, ref pass);
            allocator.EndDefragmentation(ctx, out _);
            allocator.Dispose();
        }
    }
}
