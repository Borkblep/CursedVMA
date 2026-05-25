// C# port of VmaDefragmentationContext_T from vk_mem_alloc.cpp.
// Implements the three-stage defragmentation loop:
//   BeginPass  – plans a set of moves (src → tmp dst) from the emptiest block.
//   EndPass    – applies caller-annotated results: Copy swaps, Ignore/Destroy
//                releases the reserved tmp slot and optionally the src slot.
//   Dispose    – releases any un-consumed tmp allocations left over if the
//                caller aborts between BeginPass and EndDefragmentation.
//
// Four algorithm modes are supported, selected via VmaDefragmentationInfo.Flags:
//   Fast       – one source block per vector, chosen by highest free/total ratio.
//   Balanced   – one source block per vector, chosen by highest absolute free bytes
//                (default when no algorithm flag is set).
//   Full       – all blocks with free space per vector per pass, sorted emptiest
//                first; more allocations moved per pass than Balanced.
//   Extensive  – like Full but may create a new VkDeviceMemory block when an
//                allocation cannot fit in any existing block.
//
// Only block-backed (non-dedicated) allocations are defragmented. Mapped
// allocations (MapCount > 0) are skipped.

using CursedVMA.Internal;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;

namespace CursedVMA
{
    /// <summary>
    /// Transient context for a defragmentation session. Created by
    /// <c>VmaAllocator.BeginDefragmentation</c> and consumed pass-by-pass via
    /// <c>BeginDefragmentationPass</c> / <c>EndDefragmentationPass</c>, then
    /// terminated by <c>EndDefragmentation</c>. Equivalent to the C++
    /// <c>VmaDefragmentationContext_T</c>.
    /// </summary>
    public sealed class VmaDefragmentationContext : IDisposable
    {
        private readonly VmaAllocator m_Allocator;
        private readonly VmaDefragmentationInfo m_Info;

        // Move buffer reused (replaced) each pass.
        private VmaDefragmentationMove[]? m_MoveBuffer;
        // Parallel to m_MoveBuffer: the temporary dst allocation for each move.
        private VmaAllocation?[]? m_TmpAllocBuffer;
        // Parallel: true when the tmp slot was taken over by a Copy (don't free).
        private bool[]? m_TmpConsumed;
        private uint m_MoveCount;

        private VmaDefragmentationStats m_Stats;
        private bool m_IsDisposed;

        internal VmaDefragmentationContext(
            VmaAllocator allocator, in VmaDefragmentationInfo info)
        {
            m_Allocator = allocator;
            m_Info      = info;
        }

        internal void GetStats(out VmaDefragmentationStats stats) => stats = m_Stats;

        // ── BeginPass ────────────────────────────────────────────────────────

        internal Result BeginPass(out VmaDefragmentationPassMoveInfo passInfo)
        {
            // Release any unconsumed tmps from the previous pass before planning anew.
            FreePreviousPassTemps();

            uint  maxAllocs = m_Info.MaxAllocationsPerPass == 0
                ? uint.MaxValue : m_Info.MaxAllocationsPerPass;
            ulong maxBytes  = m_Info.MaxBytesPerPass == 0
                ? ulong.MaxValue : m_Info.MaxBytesPerPass;

            VmaDefragmentationFlags algo = EffectiveAlgorithm;
            bool allowNewBlock = algo == VmaDefragmentationFlags.AlgorithmExtensiveBit;

            // Phase 1 – collect candidate (alloc, srcBlock, bv) triples without
            // touching the metadata. Iterating metadata BEFORE allocating any temp
            // slots ensures that freshly placed temps are never mistaken for source
            // allocations when a later source block is processed in the same pass.
            var candidates = new List<(VmaAllocation alloc,
                                       VmaDeviceMemoryBlock srcBlock,
                                       VmaBlockVector bv)>();
            bool breakRequested = false;
            foreach (var bv in CollectBlockVectors())
            {
                if (breakRequested) break;
                VmaDeviceMemoryBlock[] snapshot = bv.GetBlockSnapshot();
                List<VmaDeviceMemoryBlock> sourceBlocks = SelectSourceBlocks(snapshot, algo);
                foreach (var srcBlock in sourceBlocks)
                {
                    if (breakRequested) break;
                    ulong handle = srcBlock.Metadata.GetAllocationListBegin();
                    while (handle != 0)
                    {
                        var alloc = srcBlock.Metadata.GetAllocationUserData(handle)
                            as VmaAllocation;
                        handle = srcBlock.Metadata.GetNextAllocation(handle);
                        if (alloc == null || alloc.MapCount > 0) continue;
                        if (m_Info.PfnBreakCallback != null
                            && m_Info.PfnBreakCallback(m_Info.BreakCallbackUserData))
                        {
                            breakRequested = true;
                            break;
                        }
                        candidates.Add((alloc, srcBlock, bv));
                    }
                }
            }

            // Phase 2 – for each candidate, try to place a temp allocation and
            // record a Copy move. This runs after all source allocs are snapshotted,
            // so no freshly-created temp can appear as a source.
            var moves = new List<VmaDefragmentationMove>();
            var tmps  = new List<VmaAllocation?>();
            foreach (var (alloc, srcBlock, bv) in candidates)
            {
                if (alloc.Size > maxBytes) continue;
                if ((uint)moves.Count >= maxAllocs) break;

                if (bv.TryAllocateForDefrag(alloc.Size, alloc.Alignment,
                        VmaSuballocationType.Unknown, srcBlock,
                        allowNewBlock, out VmaAllocation? tmp))
                {
                    moves.Add(new VmaDefragmentationMove
                    {
                        Operation        = VmaDefragmentationMoveOperation.Copy,
                        SrcAllocation    = alloc,
                        DstTmpAllocation = tmp,
                    });
                    tmps.Add(tmp);
                    maxBytes -= alloc.Size;
                }
            }

            m_MoveCount      = (uint)moves.Count;
            m_MoveBuffer     = moves.Count > 0 ? moves.ToArray()
                                               : Array.Empty<VmaDefragmentationMove>();
            m_TmpAllocBuffer = tmps.Count  > 0 ? tmps.ToArray()
                                               : Array.Empty<VmaAllocation?>();
            m_TmpConsumed    = new bool[m_MoveCount];

            passInfo = new VmaDefragmentationPassMoveInfo
            {
                MoveCount = m_MoveCount,
                Moves     = m_MoveBuffer,
            };
            return Result.Success;
        }

        // ── EndPass ──────────────────────────────────────────────────────────

        internal Result EndPass(ref VmaDefragmentationPassMoveInfo passInfo)
        {
            if (passInfo.Moves == null || passInfo.MoveCount == 0)
                return Result.Success;

            uint count = Math.Min(passInfo.MoveCount, m_MoveCount);
            for (uint i = 0; i < count; i++)
            {
                ref VmaDefragmentationMove move = ref passInfo.Moves[i];
                if (move.SrcAllocation == null) continue;

                switch (move.Operation)
                {
                    case VmaDefragmentationMoveOperation.Copy:
                    {
                        if (move.DstTmpAllocation == null) break;

                        // Snapshot src's current location before mutating it.
                        VmaBlockVector       oldBv     = move.SrcAllocation.OwningBlockVector;
                        VmaDeviceMemoryBlock oldBlock  = move.SrcAllocation.Block;
                        ulong                oldHandle = move.SrcAllocation.AllocHandle;

                        // Repoint src allocation to the destination slot.
                        move.SrcAllocation.SwapToBlock(
                            move.DstTmpAllocation.OwningBlockVector,
                            move.DstTmpAllocation.Block,
                            move.DstTmpAllocation.AllocHandle,
                            move.DstTmpAllocation.Offset);

                        // Update back-reference so the new slot points to src.
                        move.SrcAllocation.OwningBlockVector.UpdateAllocationUserData(
                            move.SrcAllocation.Block,
                            move.SrcAllocation.AllocHandle,
                            move.SrcAllocation);

                        // Free the old slot; destroy the old block if now empty.
                        ulong freed = oldBv.FreeAllocationFromBlock(oldBlock, oldHandle);
                        if (freed > 0)
                        {
                            m_Stats.BytesFreed += freed;
                            m_Stats.DeviceMemoryBlocksFreed++;
                        }

                        // Prevent cleanup from double-freeing the consumed tmp slot.
                        m_TmpConsumed![i] = true;

                        m_Stats.BytesMoved += move.SrcAllocation.Size;
                        m_Stats.AllocationsMoved++;
                        break;
                    }

                    case VmaDefragmentationMoveOperation.Ignore:
                        // Caller couldn't perform the move; release the reserved tmp.
                        FreeTmp(i);
                        break;

                    case VmaDefragmentationMoveOperation.Destroy:
                    {
                        // Caller discards the allocation; free its old slot then the tmp.
                        VmaBlockVector       bv     = move.SrcAllocation.OwningBlockVector;
                        VmaDeviceMemoryBlock block  = move.SrcAllocation.Block;
                        ulong                handle = move.SrcAllocation.AllocHandle;

                        ulong freed = bv.FreeAllocationFromBlock(block, handle);
                        if (freed > 0)
                        {
                            m_Stats.BytesFreed += freed;
                            m_Stats.DeviceMemoryBlocksFreed++;
                        }

                        FreeTmp(i);
                        break;
                    }
                }
            }

            return Result.Success;
        }

        // ── IDisposable ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Dispose()
        {
            if (m_IsDisposed) return;
            m_IsDisposed = true;
            FreePreviousPassTemps();
        }

        // ── Private helpers ──────────────────────────────────────────────────

        // Returns the effective algorithm, defaulting to Balanced when no flag is set.
        private VmaDefragmentationFlags EffectiveAlgorithm
        {
            get
            {
                VmaDefragmentationFlags algo =
                    m_Info.Flags & VmaDefragmentationFlags.AlgorithmMask;
                return algo == VmaDefragmentationFlags.None
                    ? VmaDefragmentationFlags.AlgorithmBalancedBit
                    : algo;
            }
        }

        // Returns the ordered list of source blocks for one pass, based on algorithm.
        private static List<VmaDeviceMemoryBlock> SelectSourceBlocks(
            VmaDeviceMemoryBlock[] blocks, VmaDefragmentationFlags algo)
        {
            if (algo == VmaDefragmentationFlags.AlgorithmFullBit ||
                algo == VmaDefragmentationFlags.AlgorithmExtensiveBit)
            {
                // All non-empty blocks with free space, sorted by descending free bytes.
                var result = new List<(VmaDeviceMemoryBlock block, ulong freeBytes)>();
                foreach (var block in blocks)
                {
                    if (block.IsEmpty()) continue;
                    ulong free = block.Metadata.GetSumFreeSize();
                    if (free > 0) result.Add((block, free));
                }
                result.Sort((a, b) => b.freeBytes.CompareTo(a.freeBytes));
                var ordered = new List<VmaDeviceMemoryBlock>(result.Count);
                foreach (var (b, _) in result) ordered.Add(b);
                return ordered;
            }
            else
            {
                // Fast or Balanced: single block selected by different heuristics.
                VmaDeviceMemoryBlock? single = algo == VmaDefragmentationFlags.AlgorithmFastBit
                    ? FindSourceBlockByRatio(blocks)
                    : FindSourceBlock(blocks);
                var result = new List<VmaDeviceMemoryBlock>(1);
                if (single != null) result.Add(single);
                return result;
            }
        }

        private void FreePreviousPassTemps()
        {
            if (m_TmpAllocBuffer == null) return;
            for (uint i = 0; i < m_MoveCount; i++)
            {
                if (!(m_TmpConsumed?[i] ?? false) && m_TmpAllocBuffer[i] != null)
                {
                    m_Allocator.FreeMemory(m_TmpAllocBuffer[i]);
                    m_TmpAllocBuffer[i] = null;
                }
            }
            m_TmpAllocBuffer = null;
            m_TmpConsumed    = null;
            m_MoveCount      = 0;
        }

        private void FreeTmp(uint index)
        {
            if (m_TmpAllocBuffer != null
                && index < (uint)m_TmpAllocBuffer.Length
                && m_TmpAllocBuffer[index] != null
                && !(m_TmpConsumed?[index] ?? false))
            {
                m_Allocator.FreeMemory(m_TmpAllocBuffer[index]);
                m_TmpAllocBuffer[index] = null;
                m_TmpConsumed![index]   = true;
            }
        }

        private List<VmaBlockVector> CollectBlockVectors()
        {
            var result = new List<VmaBlockVector>();
            if (m_Info.Pool != null)
            {
                result.Add(m_Info.Pool.BlockVector);
            }
            else
            {
                uint typeCount = m_Allocator.MemoryTypeCount;
                for (uint i = 0; i < typeCount; i++)
                {
                    VmaBlockVector? bv = m_Allocator.GetDefaultBlockVector(i);
                    if (bv != null)
                        result.Add(bv);
                }
            }
            return result;
        }

        // Selects the non-empty block with the most free bytes (Balanced).
        private static VmaDeviceMemoryBlock? FindSourceBlock(VmaDeviceMemoryBlock[] blocks)
        {
            VmaDeviceMemoryBlock? best         = null;
            ulong                 bestFreeBytes = 0;
            foreach (var block in blocks)
            {
                if (block.IsEmpty()) continue;
                ulong free = block.Metadata.GetSumFreeSize();
                if (best == null || free > bestFreeBytes)
                {
                    best          = block;
                    bestFreeBytes = free;
                }
            }
            return best;
        }

        // Selects the non-empty block with the highest free/total ratio (Fast).
        private static VmaDeviceMemoryBlock? FindSourceBlockByRatio(VmaDeviceMemoryBlock[] blocks)
        {
            VmaDeviceMemoryBlock? best      = null;
            double                bestRatio = 0.0;
            foreach (var block in blocks)
            {
                if (block.IsEmpty()) continue;
                ulong total = block.Metadata.GetSize();
                if (total == 0) continue;
                ulong free = block.Metadata.GetSumFreeSize();
                if (free == 0) continue;
                double ratio = (double)free / total;
                if (ratio > bestRatio)
                {
                    bestRatio = ratio;
                    best      = block;
                }
            }
            return best;
        }
    }
}
