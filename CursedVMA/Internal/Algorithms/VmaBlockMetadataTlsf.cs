// Ports VmaBlockMetadata_TLSF from vk_mem_alloc.cpp. Two-Level Segregated Fit
// allocator: O(1) amortised alloc/free via a two-level bitmask over a segregated
// free list. A special "null block" tracks the trailing unused region and is
// never placed in the free lists; it acts as the allocation fallback.
//
// Handle encoding: allocHandle = byteOffset + 1  (0 = null/invalid).
// TlsfBlock instances form a doubly-linked physical chain ordered by ascending
// offset. Free blocks are also linked through per-bin singly-linked free lists;
// the null block is excluded from those lists.

using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CursedVMA.Internal.Algorithms
{
    internal sealed class VmaBlockMetadataTlsf : VmaBlockMetadata
    {
        // One node in both the physical chain and the free-list chain.
        private sealed class TlsfBlock
        {
            public ulong Offset;
            public ulong Size;
            public TlsfBlock? PrevPhys;
            public TlsfBlock? NextPhys;
            // Free-list links; valid only when IsFree && block != NullBlock.
            public TlsfBlock? PrevFree;
            public TlsfBlock? NextFree;
            // Valid only when !IsFree.
            public object? UserData;
            public VmaSuballocationType AllocType;
            public bool IsFree;
        }

        // Two-level constants.
        // FL 0 : sizes [0, SmallBufferSizeMax) → SlSize equal-width sublists.
        // FL n≥1: sizes [2^(SmallBufferSizeMaxLog2+n-1), 2^(SmallBufferSizeMaxLog2+n)).
        private const int SecondLevelIndex      = 5;
        private const int SlSize                = 1 << SecondLevelIndex;   // 32
        private const int SmallBufferSizeMaxLog2 = 8;
        private const ulong SmallBufferSizeMax  = 1UL << SmallBufferSizeMaxLog2; // 256
        private const int FlIndexMax            = 30;
        private const int FlIndexCount          = FlIndexMax + 1;           // 31
        private const int FreeListCount         = FlIndexCount * SlSize;    // 992

        // Physical chain head (always the block at offset 0).
        private TlsfBlock m_PhysicalChainHead = null!;
        // Trailing free region; never placed in free lists.
        private TlsfBlock m_NullBlock = null!;

        // Two-level bitmasks.
        // bit fl in m_IsFlUsed is set iff m_IsFreeBitmask[fl] != 0.
        private uint m_IsFlUsed;
        // m_IsFreeBitmask[fl]: bit sl set iff m_FreeList[fl*SlSize+sl] != null.
        private readonly uint[] m_IsFreeBitmask = new uint[FlIndexCount];
        // Segregated free-list heads.
        private readonly TlsfBlock?[] m_FreeList = new TlsfBlock?[FreeListCount];

        // O(1) handle resolution for live allocations: offset → block.
        private readonly Dictionary<ulong, TlsfBlock> m_Allocations =
            new Dictionary<ulong, TlsfBlock>();

        // Running counters updated by Alloc/Free.
        private nuint m_AllocationCount;
        private nuint m_BlocksFreeCount; // free blocks in the free lists (not null block)
        private ulong m_SumFreeSize;

        public VmaBlockMetadataTlsf(ulong bufferImageGranularity, bool isVirtual, ulong debugMargin = 0)
            : base(bufferImageGranularity, isVirtual, debugMargin) { }

        // ── Lifecycle ────────────────────────────────────────────────────────────

        public override void Init(ulong size)
        {
            base.Init(size);
            m_NullBlock = new TlsfBlock { Offset = 0, Size = size, IsFree = true };
            m_PhysicalChainHead = m_NullBlock;
            m_SumFreeSize = size;
        }

        public override void Clear()
        {
            m_AllocationCount = 0;
            m_BlocksFreeCount = 0;
            m_SumFreeSize = m_Size;
            m_Allocations.Clear();
            Array.Clear(m_IsFreeBitmask, 0, FlIndexCount);
            Array.Clear(m_FreeList, 0, FreeListCount);
            m_IsFlUsed = 0;
            m_NullBlock = new TlsfBlock { Offset = 0, Size = m_Size, IsFree = true };
            m_PhysicalChainHead = m_NullBlock;
        }

        // ── Queries ──────────────────────────────────────────────────────────────

        public override ulong GetAllocationOffset(ulong allocHandle) => allocHandle - 1;
        public override ulong GetSumFreeSize() => m_SumFreeSize;
        public override nuint GetAllocationCount() => m_AllocationCount;
        public override bool IsEmpty() => m_AllocationCount == 0;
        public override nuint GetFreeRegionsCount() => m_BlocksFreeCount + 1; // +1 for null block

        public override void GetAllocationInfo(ulong allocHandle, out VmaVirtualAllocationInfo outInfo)
        {
            ulong offset = allocHandle - 1;
            if (!m_Allocations.TryGetValue(offset, out TlsfBlock? block))
                throw new InvalidOperationException($"Allocation not found at offset {offset}.");
            outInfo = new VmaVirtualAllocationInfo
            {
                Offset = offset, Size = block.Size, UserData = block.UserData,
            };
        }

        public override ulong GetAllocationListBegin()
        {
            for (var b = m_PhysicalChainHead; b != null; b = b.NextPhys)
                if (!b.IsFree) return b.Offset + 1;
            return 0;
        }

        public override ulong GetNextAllocation(ulong prevAlloc)
        {
            ulong offset = prevAlloc - 1;
            if (!m_Allocations.TryGetValue(offset, out TlsfBlock? block)) return 0;
            for (var b = block.NextPhys; b != null; b = b.NextPhys)
                if (!b.IsFree) return b.Offset + 1;
            return 0;
        }

        public override ulong GetNextFreeRegionSize(ulong alloc)
        {
            ulong offset = alloc - 1;
            if (!m_Allocations.TryGetValue(offset, out TlsfBlock? block)) return 0;
            return block.NextPhys is { IsFree: true } next ? next.Size : 0;
        }

        // ── Statistics ───────────────────────────────────────────────────────────

        public override void AddStatistics(ref VmaStatistics stats)
        {
            stats.BlockCount++;
            stats.BlockBytes += m_Size;
            stats.AllocationCount += (uint)m_AllocationCount;
            stats.AllocationBytes += m_Size - m_SumFreeSize;
        }

        public override void AddDetailedStatistics(ref VmaDetailedStatistics stats)
        {
            stats.Statistics.BlockCount++;
            stats.Statistics.BlockBytes += m_Size;
            for (var b = m_PhysicalChainHead; b != null; b = b.NextPhys)
            {
                if (b.IsFree)
                    VmaStatisticsHelper.AddDetailedStatisticsUnusedRange(ref stats, b.Size);
                else
                    VmaStatisticsHelper.AddDetailedStatisticsAllocation(ref stats, b.Size);
            }
        }

        // ── Validate ─────────────────────────────────────────────────────────────

        public override bool Validate()
        {
            ulong expectedOffset = 0;
            nuint liveCnt = 0;
            nuint freeCnt = 0;
            ulong freeBytes = 0;
            TlsfBlock? prev = null;

            for (var b = m_PhysicalChainHead; b != null; b = b.NextPhys)
            {
                Assert(b.Offset == expectedOffset,
                    $"Block at expected offset {expectedOffset} has offset {b.Offset}.");
                Assert(b.Size > 0 || b == m_NullBlock, "Non-null block must have size > 0.");
                Assert(b.PrevPhys == prev, "Physical chain prev-link mismatch.");

                if (b.IsFree)
                {
                    freeBytes += b.Size;
                    if (b != m_NullBlock)
                    {
                        freeCnt++;
                        // Verify block is reachable from its free-list head.
                        var (fl, sl) = SizeToFLSLIndex(b.Size);
                        bool found = false;
                        for (var cur = m_FreeList[fl * SlSize + sl]; cur != null; cur = cur.NextFree)
                            if (cur == b) { found = true; break; }
                        Assert(found, $"Free block at {b.Offset} not found in its free list.");
                    }
                }
                else
                {
                    liveCnt++;
                    Assert(m_Allocations.ContainsKey(b.Offset),
                        $"Live block at {b.Offset} missing from m_Allocations.");
                }

                expectedOffset = b.Offset + b.Size;
                prev = b;
            }

            Assert(expectedOffset == m_Size, "Physical chain doesn't cover the full block.");
            Assert(liveCnt == m_AllocationCount, "Allocation count mismatch.");
            Assert(freeCnt == m_BlocksFreeCount, "BlocksFreeCount mismatch.");
            Assert(freeBytes == m_SumFreeSize, "SumFreeSize mismatch.");
            return true;
        }

        // ── Corruption detection ─────────────────────────────────────────────────

        public override unsafe Result CheckCorruption(byte* pBlockData)
        {
            if (m_DebugMargin == 0) return Result.ErrorFeatureNotPresent;

            foreach (var block in m_Allocations.Values)
            {
                if (!ValidateMagicValue(pBlockData, block.Offset, m_DebugMargin))
                    return Result.ErrorUnknown;
                if (!ValidateMagicValue(pBlockData, block.Offset + block.Size - m_DebugMargin, m_DebugMargin))
                    return Result.ErrorUnknown;
            }
            return Result.Success;
        }

        // ── Allocation ───────────────────────────────────────────────────────────

        public override bool CreateAllocationRequest(
            ulong allocSize,
            ulong allocAlignment,
            bool upperAddress,
            VmaSuballocationType allocType,
            uint strategy,
            out VmaAllocationRequest request)
        {
            request = default;
            // TLSF does not support upper-address allocations.
            if (allocSize == 0 || allocSize > m_Size || upperAddress) return false;

            ulong paddedSize = allocSize + 2 * m_DebugMargin;
            ulong searchSize = RoundUpToTlsfClass(paddedSize);
            TlsfBlock? candidate = SearchFreeBlock(searchSize);

            if (candidate != null
                && TryFitBlock(candidate, allocSize, allocAlignment, allocType, out ulong off))
            {
                request = new VmaAllocationRequest
                {
                    AllocHandle = off + 1,
                    Size = paddedSize,
                    CustomData = candidate,
                    Type = VmaAllocationRequestType.TLSF,
                };
                return true;
            }

            // Fallback: carve from the null block.
            if (TryFitBlock(m_NullBlock, allocSize, allocAlignment, allocType, out ulong nullOff))
            {
                request = new VmaAllocationRequest
                {
                    AllocHandle = nullOff + 1,
                    Size = paddedSize,
                    CustomData = m_NullBlock,
                    Type = VmaAllocationRequestType.TLSF,
                };
                return true;
            }

            return false;
        }

        private bool TryFitBlock(
            TlsfBlock block, ulong allocSize, ulong allocAlignment,
            VmaSuballocationType allocType, out ulong resultOffset)
        {
            resultOffset = 0;
            ulong offset = VmaMath.AlignUp(block.Offset, allocAlignment);

            if (m_BufferImageGranularity > 1 && block.PrevPhys is { IsFree: false } prevLive)
            {
                if (VmaMath.BlocksOnSamePage(prevLive.Offset, prevLive.Size, offset, m_BufferImageGranularity)
                    && VmaMath.IsBufferImageGranularityConflict(prevLive.AllocType, allocType))
                {
                    offset = VmaMath.AlignUp(offset, m_BufferImageGranularity);
                }
            }

            if (offset + allocSize + 2 * m_DebugMargin > block.Offset + block.Size) return false;

            if (m_BufferImageGranularity > 1 && block.NextPhys is { IsFree: false } nextLive)
            {
                if (VmaMath.BlocksOnSamePage(offset, allocSize, nextLive.Offset, m_BufferImageGranularity)
                    && VmaMath.IsBufferImageGranularityConflict(allocType, nextLive.AllocType))
                {
                    return false;
                }
            }

            resultOffset = offset;
            return true;
        }

        public override void Alloc(in VmaAllocationRequest request, VmaSuballocationType type, object? userData)
        {
            Assert(request.Type == VmaAllocationRequestType.TLSF,
                $"Expected TLSF request type, got {request.Type}.");

            var block = (TlsfBlock)request.CustomData!;
            ulong offset = request.AllocHandle - 1;
            bool isNullBlock = block == m_NullBlock;

            if (!isNullBlock) RemoveFreeBlock(block);

            // Split off leading alignment-padding as a new free block.
            if (offset > block.Offset)
            {
                var pad = new TlsfBlock
                {
                    Offset = block.Offset,
                    Size = offset - block.Offset,
                    PrevPhys = block.PrevPhys,
                    NextPhys = block,
                    IsFree = true,
                };
                if (block.PrevPhys != null) block.PrevPhys.NextPhys = pad;
                else m_PhysicalChainHead = pad;
                block.PrevPhys = pad;
                block.Offset = offset;
                block.Size -= pad.Size;
                InsertFreeBlock(pad);
            }

            // Split off trailing remainder.
            ulong allocEnd = offset + request.Size;
            ulong blockEnd = block.Offset + block.Size;

            if (blockEnd > allocEnd)
            {
                var rem = new TlsfBlock
                {
                    Offset = allocEnd,
                    Size = blockEnd - allocEnd,
                    PrevPhys = block,
                    NextPhys = block.NextPhys,
                    IsFree = true,
                };
                if (block.NextPhys != null) block.NextPhys.PrevPhys = rem;
                block.NextPhys = rem;
                block.Size = request.Size;

                if (isNullBlock)
                    m_NullBlock = rem;
                else
                    InsertFreeBlock(rem);
            }
            else if (isNullBlock)
            {
                // Null block fully consumed; create a zero-size sentinel tail.
                var sentinel = new TlsfBlock
                {
                    Offset = allocEnd,
                    Size = 0,
                    PrevPhys = block,
                    NextPhys = null,
                    IsFree = true,
                };
                block.NextPhys = sentinel;
                block.Size = request.Size;
                m_NullBlock = sentinel;
            }

            block.IsFree = false;
            block.UserData = userData;
            block.AllocType = type;

            m_Allocations[offset] = block;
            m_AllocationCount++;
            m_SumFreeSize -= request.Size;
        }

        public override void Free(ulong allocHandle)
        {
            ulong offset = allocHandle - 1;
            if (!m_Allocations.Remove(offset, out TlsfBlock? block))
                throw new InvalidOperationException($"Allocation not found at offset {offset}.");

            m_AllocationCount--;
            m_SumFreeSize += block.Size;
            block.IsFree = true;
            block.UserData = null;

            FreeBlock(block);
        }

        private void FreeBlock(TlsfBlock block)
        {
            // Coalesce left: merge with a free left neighbour (never the null block,
            // which is always the rightmost physical block).
            if (block.PrevPhys != null && block.PrevPhys.IsFree)
            {
                var left = block.PrevPhys;
                RemoveFreeBlock(left);
                left.Size += block.Size;
                left.NextPhys = block.NextPhys;
                if (block.NextPhys != null) block.NextPhys.PrevPhys = left;
                block = left;
            }

            // Coalesce right: merge with a free right neighbour or the null block.
            if (block.NextPhys != null && block.NextPhys.IsFree)
            {
                var right = block.NextPhys;
                if (right == m_NullBlock)
                {
                    block.Size += right.Size;
                    block.NextPhys = null;
                    m_NullBlock = block;
                    return; // block is the new null block; no free-list insertion
                }
                RemoveFreeBlock(right);
                block.Size += right.Size;
                block.NextPhys = right.NextPhys;
                if (right.NextPhys != null) right.NextPhys.PrevPhys = block;

                // The right merge may now place us adjacent to the null block.
                if (block.NextPhys == m_NullBlock)
                {
                    block.Size += m_NullBlock.Size;
                    block.NextPhys = null;
                    m_NullBlock = block;
                    return;
                }
            }

            InsertFreeBlock(block);
        }

        // ── UserData ─────────────────────────────────────────────────────────────

        public override object? GetAllocationUserData(ulong allocHandle)
        {
            ulong offset = allocHandle - 1;
            if (!m_Allocations.TryGetValue(offset, out TlsfBlock? block))
                throw new InvalidOperationException($"Allocation not found at offset {offset}.");
            return block.UserData;
        }

        public override void SetAllocationUserData(ulong allocHandle, object? userData)
        {
            ulong offset = allocHandle - 1;
            if (m_Allocations.TryGetValue(offset, out TlsfBlock? block))
                block.UserData = userData;
        }

        // ── TLSF helpers ─────────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (int fl, int sl) SizeToFLSLIndex(ulong size)
        {
            if (size < SmallBufferSizeMax)
                return (0, (int)(size >> (SmallBufferSizeMaxLog2 - SecondLevelIndex)));

            int msb = VmaMath.BitScanMSB(size);
            int fl = msb - SmallBufferSizeMaxLog2 + 1;
            if (fl > FlIndexMax) fl = FlIndexMax; // clamp for very large blocks
            int sl = (int)((size >> (msb - SecondLevelIndex)) & (SlSize - 1));
            return (fl, sl);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong RoundUpToTlsfClass(ulong size)
        {
            if (size < SmallBufferSizeMax)
            {
                const ulong quantum = SmallBufferSizeMax / SlSize; // 8 bytes
                return (size + quantum - 1) & ~(quantum - 1);
            }
            int msb = VmaMath.BitScanMSB(size);
            ulong mask = (1ul << (msb - SecondLevelIndex)) - 1;
            return (size + mask) & ~mask;
        }

        private TlsfBlock? SearchFreeBlock(ulong size)
        {
            var (fl, sl) = SizeToFLSLIndex(size);

            // Try same FL, starting from sl and going up.
            uint slMask = m_IsFreeBitmask[fl] & (~0u << sl);
            if (slMask != 0)
                return m_FreeList[fl * SlSize + VmaMath.BitScanLSB(slMask)];

            // Try higher FLs.
            uint flMask = fl < 31 ? m_IsFlUsed & (~0u << (fl + 1)) : 0u;
            if (flMask != 0)
            {
                int flBit = VmaMath.BitScanLSB(flMask);
                return m_FreeList[flBit * SlSize + VmaMath.BitScanLSB(m_IsFreeBitmask[flBit])];
            }
            return null;
        }

        private void InsertFreeBlock(TlsfBlock block)
        {
            Assert(block != m_NullBlock, "NullBlock must not be placed in the free lists.");
            var (fl, sl) = SizeToFLSLIndex(block.Size);
            int idx = fl * SlSize + sl;
            block.PrevFree = null;
            block.NextFree = m_FreeList[idx];
            if (m_FreeList[idx] != null) m_FreeList[idx]!.PrevFree = block;
            m_FreeList[idx] = block;
            m_IsFreeBitmask[fl] |= 1u << sl;
            m_IsFlUsed |= 1u << fl;
            m_BlocksFreeCount++;
        }

        private void RemoveFreeBlock(TlsfBlock block)
        {
            Assert(block != m_NullBlock, "NullBlock is not in the free lists.");
            var (fl, sl) = SizeToFLSLIndex(block.Size);
            int idx = fl * SlSize + sl;
            if (block.PrevFree != null) block.PrevFree.NextFree = block.NextFree;
            else m_FreeList[idx] = block.NextFree;
            if (block.NextFree != null) block.NextFree.PrevFree = block.PrevFree;
            block.PrevFree = null;
            block.NextFree = null;
            if (m_FreeList[idx] == null)
            {
                m_IsFreeBitmask[fl] &= ~(1u << sl);
                if (m_IsFreeBitmask[fl] == 0) m_IsFlUsed &= ~(1u << fl);
            }
            m_BlocksFreeCount--;
        }
    }
}
