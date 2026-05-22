// C# port of VmaDefragmentationContext_T from vk_mem_alloc.cpp.
// Implements the three-stage defragmentation loop:
//   BeginPass  – plans a set of moves (src → tmp dst) from the emptiest block.
//   EndPass    – applies caller-annotated results: Copy swaps, Ignore/Destroy
//                releases the reserved tmp slot and optionally the src slot.
//   Dispose    – releases any un-consumed tmp allocations left over if the
//                caller aborts between BeginPass and EndDefragmentation.
//
// Only block-backed (non-dedicated) allocations are defragmented. Mapped
// allocations (MapCount > 0) are skipped. No new blocks are created during
// a pass; if a source allocation cannot fit in any existing block it is
// silently omitted from the move list.

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

            var moves = new List<VmaDefragmentationMove>();
            var tmps  = new List<VmaAllocation?>();
            bool limitReached = false;

            foreach (var bv in CollectBlockVectors())
            {
                if (limitReached) break;

                VmaDeviceMemoryBlock? srcBlock = FindSourceBlock(bv.GetBlockSnapshot());
                if (srcBlock == null) continue;

                ulong handle = srcBlock.Metadata.GetAllocationListBegin();
                while (handle != 0 && !limitReached)
                {
                    var alloc = srcBlock.Metadata.GetAllocationUserData(handle) as VmaAllocation;
                    // Advance now so continue/break don't skip a handle.
                    handle = srcBlock.Metadata.GetNextAllocation(handle);

                    if (alloc == null || alloc.MapCount > 0 || alloc.Size > maxBytes)
                        continue;

                    if ((uint)moves.Count >= maxAllocs)
                    {
                        limitReached = true;
                        break;
                    }

                    if (m_Info.PfnBreakCallback != null
                        && m_Info.PfnBreakCallback(m_Info.BreakCallbackUserData))
                    {
                        limitReached = true;
                        break;
                    }

                    if (bv.TryAllocateInExistingBlocks(
                            alloc.Size, alloc.Alignment,
                            VmaSuballocationType.Unknown, srcBlock,
                            out VmaAllocation? tmp))
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

        // Returns the non-empty block with the most free bytes (best candidate to drain).
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
                    best         = block;
                    bestFreeBytes = free;
                }
            }
            return best;
        }
    }
}
