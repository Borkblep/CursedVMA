// Ports VmaBlockMetadata_Linear from vk_mem_alloc.cpp. A bump-pointer allocator
// that operates in one of three modes:
//   • Single-stack  – allocations appended to the end of the 1st vector.
//   • Double-stack  – 1st vector grows from offset 0; 2nd vector from block-end
//                     downward (UpperAddress flag selects this path).
//   • Ring-buffer   – 2nd vector wraps around from offset 0 after the front of
//                     the 1st vector has been freed; used by defragmentation.
//
// Handle encoding: allocHandle = byteOffset + 1  (0 is the null/invalid handle).
// The 1st vector is always sorted by ascending offset; the 2nd vector is either
// ascending (ring-buffer) or descending (double-stack), so both can be searched
// with a binary search adapted to their ordering.

using System;
using System.Collections.Generic;

namespace CursedVMA.Internal.Algorithms
{
    internal sealed class VmaBlockMetadataLinear : VmaBlockMetadata
    {
        private enum SecondVectorMode : byte { Empty, RingBuffer, DoubleStack }

        private readonly List<VmaSuballocation> m_Suballocations1;
        private readonly List<VmaSuballocation> m_Suballocations2;

        // 0 → m_Suballocations1 is the first (low-address) vector; 1 → swapped.
        private int m_1stVectorIndex;
        private SecondVectorMode m_2ndVectorMode;

        // Null-item counters for the 1st vector.
        private int m_1stNullItemsBeginCount;   // consecutive nulls at the start
        private int m_1stNullItemsMiddleCount;  // scattered nulls after the start
        private int m_2ndNullItemsCount;

        // Kept in sync by Alloc/Free so GetSumFreeSize is O(1).
        private ulong m_SumFreeSize;

        public VmaBlockMetadataLinear(ulong bufferImageGranularity, bool isVirtual)
            : base(bufferImageGranularity, isVirtual)
        {
            m_Suballocations1 = new List<VmaSuballocation>();
            m_Suballocations2 = new List<VmaSuballocation>();
        }

        private List<VmaSuballocation> GetSuballocations1st() =>
            m_1stVectorIndex == 0 ? m_Suballocations1 : m_Suballocations2;

        private List<VmaSuballocation> GetSuballocations2nd() =>
            m_1stVectorIndex == 0 ? m_Suballocations2 : m_Suballocations1;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        public override void Init(ulong size)
        {
            base.Init(size);
            m_SumFreeSize = size;
        }

        public override void Clear()
        {
            m_Suballocations1.Clear();
            m_Suballocations2.Clear();
            m_1stVectorIndex = 0;
            m_2ndVectorMode = SecondVectorMode.Empty;
            m_1stNullItemsBeginCount = 0;
            m_1stNullItemsMiddleCount = 0;
            m_2ndNullItemsCount = 0;
            m_SumFreeSize = m_Size;
        }

        // ── Queries ──────────────────────────────────────────────────────────────

        public override ulong GetAllocationOffset(ulong allocHandle) => allocHandle - 1;

        public override ulong GetSumFreeSize() => m_SumFreeSize;

        public override bool IsEmpty() => GetAllocationCount() == 0;

        public override nuint GetAllocationCount()
        {
            var sub1 = GetSuballocations1st();
            int live = 0;
            for (int i = m_1stNullItemsBeginCount; i < sub1.Count; i++)
                if (sub1[i].Type != VmaSuballocationType.Free) live++;

            if (m_2ndVectorMode != SecondVectorMode.Empty)
            {
                var sub2 = GetSuballocations2nd();
                for (int i = 0; i < sub2.Count; i++)
                    if (sub2[i].Type != VmaSuballocationType.Free) live++;
            }
            return (nuint)live;
        }

        public override nuint GetFreeRegionsCount()
        {
            var sub1 = GetSuballocations1st();
            int nullCount1 = m_1stNullItemsBeginCount + m_1stNullItemsMiddleCount;
            int live1 = sub1.Count - nullCount1;

            if (m_2ndVectorMode == SecondVectorMode.Empty)
                return (nuint)(live1 + 1);

            var sub2 = GetSuballocations2nd();
            int live2 = sub2.Count - m_2ndNullItemsCount;
            return (nuint)(live1 + live2 + 2);
        }

        public override void GetAllocationInfo(ulong allocHandle, out VmaVirtualAllocationInfo outInfo)
        {
            var s = RequireSuballoc(allocHandle - 1);
            outInfo = new VmaVirtualAllocationInfo { Offset = s.Offset, Size = s.Size, UserData = s.UserData };
        }

        public override ulong GetAllocationListBegin()
        {
            var sub1 = GetSuballocations1st();
            for (int i = m_1stNullItemsBeginCount; i < sub1.Count; i++)
                if (sub1[i].Type != VmaSuballocationType.Free) return sub1[i].Offset + 1;

            if (m_2ndVectorMode == SecondVectorMode.RingBuffer)
            {
                var sub2 = GetSuballocations2nd();
                for (int i = 0; i < sub2.Count; i++)
                    if (sub2[i].Type != VmaSuballocationType.Free) return sub2[i].Offset + 1;
            }
            else if (m_2ndVectorMode == SecondVectorMode.DoubleStack)
            {
                var sub2 = GetSuballocations2nd();
                // 2nd is descending; iterate in reverse for ascending offset order.
                for (int i = sub2.Count - 1; i >= 0; i--)
                    if (sub2[i].Type != VmaSuballocationType.Free) return sub2[i].Offset + 1;
            }
            return 0;
        }

        public override ulong GetNextAllocation(ulong prevAlloc)
        {
            ulong prevOffset = prevAlloc - 1;
            var sub1 = GetSuballocations1st();
            var sub2 = GetSuballocations2nd();

            int idx = BinarySearchAsc(sub1, m_1stNullItemsBeginCount, prevOffset);
            if (idx >= 0)
            {
                for (int i = idx + 1; i < sub1.Count; i++)
                    if (sub1[i].Type != VmaSuballocationType.Free) return sub1[i].Offset + 1;

                if (m_2ndVectorMode == SecondVectorMode.RingBuffer)
                {
                    for (int i = 0; i < sub2.Count; i++)
                        if (sub2[i].Type != VmaSuballocationType.Free) return sub2[i].Offset + 1;
                }
                else if (m_2ndVectorMode == SecondVectorMode.DoubleStack)
                {
                    for (int i = sub2.Count - 1; i >= 0; i--)
                        if (sub2[i].Type != VmaSuballocationType.Free) return sub2[i].Offset + 1;
                }
                return 0;
            }

            if (m_2ndVectorMode == SecondVectorMode.RingBuffer)
            {
                int idx2 = BinarySearchAsc(sub2, 0, prevOffset);
                if (idx2 >= 0)
                {
                    for (int i = idx2 + 1; i < sub2.Count; i++)
                        if (sub2[i].Type != VmaSuballocationType.Free) return sub2[i].Offset + 1;
                }
            }
            else if (m_2ndVectorMode == SecondVectorMode.DoubleStack)
            {
                int idx2 = BinarySearchDesc(sub2, prevOffset);
                if (idx2 >= 0)
                {
                    for (int i = idx2 - 1; i >= 0; i--)
                        if (sub2[i].Type != VmaSuballocationType.Free) return sub2[i].Offset + 1;
                }
            }
            return 0;
        }

        public override ulong GetNextFreeRegionSize(ulong alloc)
        {
            ulong offset = alloc - 1;
            var sub1 = GetSuballocations1st();
            var sub2 = GetSuballocations2nd();

            int idx = BinarySearchAsc(sub1, m_1stNullItemsBeginCount, offset);
            if (idx >= 0)
            {
                ulong itemEnd = sub1[idx].Offset + sub1[idx].Size;
                for (int i = idx + 1; i < sub1.Count; i++)
                    if (sub1[i].Type != VmaSuballocationType.Free) return sub1[i].Offset - itemEnd;

                if (m_2ndVectorMode == SecondVectorMode.DoubleStack && sub2.Count > 0)
                {
                    for (int i = sub2.Count - 1; i >= 0; i--)
                        if (sub2[i].Type != VmaSuballocationType.Free) return sub2[i].Offset - itemEnd;
                }
                return m_Size - itemEnd;
            }

            if (m_2ndVectorMode == SecondVectorMode.DoubleStack)
            {
                int idx2 = BinarySearchDesc(sub2, offset);
                if (idx2 >= 0)
                {
                    ulong itemEnd = sub2[idx2].Offset + sub2[idx2].Size;
                    // In descending vector, lower indices = higher offsets.
                    for (int i = idx2 - 1; i >= 0; i--)
                        if (sub2[i].Type != VmaSuballocationType.Free) return sub2[i].Offset - itemEnd;
                    return m_Size - itemEnd;
                }
            }
            else if (m_2ndVectorMode == SecondVectorMode.RingBuffer)
            {
                int idx2 = BinarySearchAsc(sub2, 0, offset);
                if (idx2 >= 0)
                {
                    ulong itemEnd = sub2[idx2].Offset + sub2[idx2].Size;
                    for (int i = idx2 + 1; i < sub2.Count; i++)
                        if (sub2[i].Type != VmaSuballocationType.Free) return sub2[i].Offset - itemEnd;
                    int first1st = m_1stNullItemsBeginCount;
                    return first1st < sub1.Count ? sub1[first1st].Offset - itemEnd : m_Size - itemEnd;
                }
            }
            return 0;
        }

        // ── Statistics ───────────────────────────────────────────────────────────

        public override void AddStatistics(ref VmaStatistics stats)
        {
            stats.BlockCount++;
            stats.BlockBytes += m_Size;
            stats.AllocationCount += (uint)GetAllocationCount();
            stats.AllocationBytes += m_Size - m_SumFreeSize;
        }

        public override void AddDetailedStatistics(ref VmaDetailedStatistics stats)
        {
            stats.Statistics.BlockCount++;
            stats.Statistics.BlockBytes += m_Size;

            ulong lastOffset = 0;
            var sub1 = GetSuballocations1st();
            var sub2 = GetSuballocations2nd();

            if (m_2ndVectorMode == SecondVectorMode.RingBuffer)
            {
                // Ring-buffer: 2nd vector holds the lower-address "wrapped" part.
                for (int i = 0; i < sub2.Count; i++)
                    VisitSuballoc(sub2[i], ref lastOffset, ref stats);
                for (int i = m_1stNullItemsBeginCount; i < sub1.Count; i++)
                    VisitSuballoc(sub1[i], ref lastOffset, ref stats);
            }
            else
            {
                for (int i = m_1stNullItemsBeginCount; i < sub1.Count; i++)
                    VisitSuballoc(sub1[i], ref lastOffset, ref stats);
                if (m_2ndVectorMode == SecondVectorMode.DoubleStack)
                {
                    // Double-stack: 2nd is stored descending; iterate in reverse.
                    for (int i = sub2.Count - 1; i >= 0; i--)
                        VisitSuballoc(sub2[i], ref lastOffset, ref stats);
                }
            }

            if (lastOffset < m_Size)
                VmaStatisticsHelper.AddDetailedStatisticsUnusedRange(ref stats, m_Size - lastOffset);
        }

        private static void VisitSuballoc(
            VmaSuballocation s, ref ulong lastOffset, ref VmaDetailedStatistics stats)
        {
            if (s.Type == VmaSuballocationType.Free) return;
            if (s.Offset > lastOffset)
                VmaStatisticsHelper.AddDetailedStatisticsUnusedRange(ref stats, s.Offset - lastOffset);
            VmaStatisticsHelper.AddDetailedStatisticsAllocation(ref stats, s.Size);
            lastOffset = s.Offset + s.Size;
        }

        // ── Validate ─────────────────────────────────────────────────────────────

        public override bool Validate()
        {
            var sub1 = GetSuballocations1st();
            var sub2 = GetSuballocations2nd();

            // m_1stNullItemsBeginCount must equal actual leading-null count.
            int beginNulls = 0;
            while (beginNulls < sub1.Count && sub1[beginNulls].Type == VmaSuballocationType.Free)
                beginNulls++;
            Assert(beginNulls == m_1stNullItemsBeginCount, "m_1stNullItemsBeginCount mismatch");

            // m_1stNullItemsMiddleCount must equal nulls in the logical portion.
            int midNulls = 0;
            for (int i = m_1stNullItemsBeginCount; i < sub1.Count; i++)
                if (sub1[i].Type == VmaSuballocationType.Free) midNulls++;
            Assert(midNulls == m_1stNullItemsMiddleCount, "m_1stNullItemsMiddleCount mismatch");

            // Last item of 1st (if any logical items) must be live.
            if (sub1.Count > m_1stNullItemsBeginCount)
                Assert(sub1[sub1.Count - 1].Type != VmaSuballocationType.Free,
                    "Last item of 1st vector must be live after compaction");

            // 1st vector must be sorted by ascending offset.
            for (int i = m_1stNullItemsBeginCount + 1; i < sub1.Count; i++)
                Assert(sub1[i].Offset >= sub1[i - 1].Offset + sub1[i - 1].Size,
                    "1st vector: items overlap or out of order");

            // Verify SumFreeSize.
            ulong freeTotal = m_Size;
            for (int i = m_1stNullItemsBeginCount; i < sub1.Count; i++)
                if (sub1[i].Type != VmaSuballocationType.Free) freeTotal -= sub1[i].Size;
            if (m_2ndVectorMode != SecondVectorMode.Empty)
            {
                int nullCount2 = 0;
                foreach (var s in sub2)
                {
                    if (s.Type != VmaSuballocationType.Free) freeTotal -= s.Size;
                    else nullCount2++;
                }
                Assert(nullCount2 == m_2ndNullItemsCount, "m_2ndNullItemsCount mismatch");
            }
            Assert(freeTotal == m_SumFreeSize, "SumFreeSize mismatch");

            return true;
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
            if (allocSize == 0 || allocSize > m_Size)
                return false;

            return upperAddress
                ? CreateAllocationRequest_Upper(allocSize, allocAlignment, allocType, out request)
                : CreateAllocationRequest_Lower(allocSize, allocAlignment, allocType, out request);
        }

        private bool CreateAllocationRequest_Lower(
            ulong allocSize, ulong allocAlignment, VmaSuballocationType allocType,
            out VmaAllocationRequest request)
        {
            request = default;
            var sub1 = GetSuballocations1st();
            var sub2 = GetSuballocations2nd();

            if (m_2ndVectorMode == SecondVectorMode.Empty ||
                m_2ndVectorMode == SecondVectorMode.DoubleStack)
            {
                // Try appending to the end of the 1st vector.
                ulong baseOffset = sub1.Count > 0
                    ? sub1[sub1.Count - 1].Offset + sub1[sub1.Count - 1].Size
                    : 0ul;

                ulong resultOffset = VmaMath.AlignUp(baseOffset, allocAlignment);

                if (m_BufferImageGranularity > 1 && sub1.Count > m_1stNullItemsBeginCount)
                {
                    for (int i = sub1.Count - 1; i >= m_1stNullItemsBeginCount; i--)
                    {
                        var prev = sub1[i];
                        if (prev.Type == VmaSuballocationType.Free) continue;
                        if (VmaMath.BlocksOnSamePage(prev.Offset, prev.Size, resultOffset, m_BufferImageGranularity)
                            && VmaMath.IsBufferImageGranularityConflict(prev.Type, allocType))
                        {
                            resultOffset = VmaMath.AlignUp(resultOffset, m_BufferImageGranularity);
                        }
                        break;
                    }
                }

                ulong freeSpaceEnd = m_2ndVectorMode == SecondVectorMode.DoubleStack && sub2.Count > 0
                    ? sub2[sub2.Count - 1].Offset
                    : m_Size;

                if (resultOffset + allocSize > freeSpaceEnd) return false;

                if (m_BufferImageGranularity > 1 && m_2ndVectorMode == SecondVectorMode.DoubleStack
                    && sub2.Count > 0)
                {
                    var next = sub2[sub2.Count - 1];
                    if (next.Type != VmaSuballocationType.Free
                        && VmaMath.BlocksOnSamePage(resultOffset, allocSize, next.Offset, m_BufferImageGranularity)
                        && VmaMath.IsBufferImageGranularityConflict(allocType, next.Type))
                    {
                        return false;
                    }
                }

                request = new VmaAllocationRequest
                {
                    AllocHandle = resultOffset + 1,
                    Size = allocSize,
                    Type = VmaAllocationRequestType.EndOf1st,
                };
                return true;
            }
            else // SecondVectorMode.RingBuffer
            {
                // Ring-buffer: new allocations go at the end of the 2nd vector.
                ulong baseOffset = sub2.Count > 0
                    ? sub2[sub2.Count - 1].Offset + sub2[sub2.Count - 1].Size
                    : 0ul;

                ulong resultOffset = VmaMath.AlignUp(baseOffset, allocAlignment);

                if (m_BufferImageGranularity > 1 && sub2.Count > 0)
                {
                    var prev = sub2[sub2.Count - 1];
                    if (prev.Type != VmaSuballocationType.Free
                        && VmaMath.BlocksOnSamePage(prev.Offset, prev.Size, resultOffset, m_BufferImageGranularity)
                        && VmaMath.IsBufferImageGranularityConflict(prev.Type, allocType))
                    {
                        resultOffset = VmaMath.AlignUp(resultOffset, m_BufferImageGranularity);
                    }
                }

                ulong freeSpaceEnd = sub1.Count > m_1stNullItemsBeginCount
                    ? sub1[m_1stNullItemsBeginCount].Offset
                    : m_Size;

                if (resultOffset + allocSize > freeSpaceEnd) return false;

                if (m_BufferImageGranularity > 1 && sub1.Count > m_1stNullItemsBeginCount)
                {
                    var next = sub1[m_1stNullItemsBeginCount];
                    if (next.Type != VmaSuballocationType.Free
                        && VmaMath.BlocksOnSamePage(resultOffset, allocSize, next.Offset, m_BufferImageGranularity)
                        && VmaMath.IsBufferImageGranularityConflict(allocType, next.Type))
                    {
                        return false;
                    }
                }

                request = new VmaAllocationRequest
                {
                    AllocHandle = resultOffset + 1,
                    Size = allocSize,
                    Type = VmaAllocationRequestType.EndOf2nd,
                };
                return true;
            }
        }

        private bool CreateAllocationRequest_Upper(
            ulong allocSize, ulong allocAlignment, VmaSuballocationType allocType,
            out VmaAllocationRequest request)
        {
            request = default;
            if (m_2ndVectorMode == SecondVectorMode.RingBuffer) return false;

            var sub1 = GetSuballocations1st();
            var sub2 = GetSuballocations2nd();

            // Start from the top of the block and work downward.
            ulong resultOffset = sub2.Count > 0
                ? sub2[sub2.Count - 1].Offset
                : m_Size;

            if (resultOffset < allocSize) return false;
            resultOffset -= allocSize;
            resultOffset = VmaMath.AlignDown(resultOffset, allocAlignment);

            if (m_BufferImageGranularity > 1 && sub2.Count > 0)
            {
                var below = sub2[sub2.Count - 1];
                if (below.Type != VmaSuballocationType.Free
                    && VmaMath.BlocksOnSamePage(resultOffset, allocSize, below.Offset, m_BufferImageGranularity)
                    && VmaMath.IsBufferImageGranularityConflict(allocType, below.Type))
                {
                    if (resultOffset < m_BufferImageGranularity) return false;
                    resultOffset = VmaMath.AlignDown(resultOffset - 1, m_BufferImageGranularity);
                }
            }

            // Must stay above the top of the 1st vector.
            ulong freeSpaceBegin = sub1.Count > m_1stNullItemsBeginCount
                ? sub1[sub1.Count - 1].Offset + sub1[sub1.Count - 1].Size
                : 0ul;

            if (resultOffset < freeSpaceBegin || resultOffset < allocSize) return false;

            if (m_BufferImageGranularity > 1 && sub1.Count > m_1stNullItemsBeginCount)
            {
                for (int i = sub1.Count - 1; i >= m_1stNullItemsBeginCount; i--)
                {
                    var prev = sub1[i];
                    if (prev.Type == VmaSuballocationType.Free) continue;
                    if (VmaMath.BlocksOnSamePage(prev.Offset, prev.Size, resultOffset, m_BufferImageGranularity)
                        && VmaMath.IsBufferImageGranularityConflict(prev.Type, allocType))
                    {
                        return false;
                    }
                    break;
                }
            }

            request = new VmaAllocationRequest
            {
                AllocHandle = resultOffset + 1,
                Size = allocSize,
                Type = VmaAllocationRequestType.UpperAddress,
            };
            return true;
        }

        public override void Alloc(in VmaAllocationRequest request, VmaSuballocationType type, object? userData)
        {
            ulong offset = request.AllocHandle - 1;
            var newSub = new VmaSuballocation
            {
                Offset = offset,
                Size = request.Size,
                UserData = userData,
                Type = type,
            };

            switch (request.Type)
            {
                case VmaAllocationRequestType.UpperAddress:
                {
                    Assert(m_2ndVectorMode != SecondVectorMode.RingBuffer,
                        "UpperAddress cannot be used while ring-buffer mode is active.");
                    GetSuballocations2nd().Add(newSub);
                    m_2ndVectorMode = SecondVectorMode.DoubleStack;
                    break;
                }
                case VmaAllocationRequestType.EndOf1st:
                {
                    var sub1 = GetSuballocations1st();
                    Assert(sub1.Count == 0
                        || offset >= sub1[sub1.Count - 1].Offset + sub1[sub1.Count - 1].Size,
                        "EndOf1st: new item overlaps the previous one.");
                    sub1.Add(newSub);
                    break;
                }
                case VmaAllocationRequestType.EndOf2nd:
                {
                    var sub2 = GetSuballocations2nd();
                    Assert(sub2.Count == 0
                        || offset >= sub2[sub2.Count - 1].Offset + sub2[sub2.Count - 1].Size,
                        "EndOf2nd: new item overlaps the previous one.");
                    sub2.Add(newSub);
                    break;
                }
                default:
                    Assert(false, $"Unexpected VmaAllocationRequestType {request.Type}.");
                    break;
            }

            m_SumFreeSize -= request.Size;
        }

        public override void Free(ulong allocHandle)
        {
            ulong offset = allocHandle - 1;
            var sub1 = GetSuballocations1st();
            var sub2 = GetSuballocations2nd();

            int idx1 = BinarySearchAsc(sub1, m_1stNullItemsBeginCount, offset);
            if (idx1 >= 0) { FreeFrom1st(sub1, idx1); return; }

            if (m_2ndVectorMode != SecondVectorMode.Empty)
            {
                int idx2 = m_2ndVectorMode == SecondVectorMode.RingBuffer
                    ? BinarySearchAsc(sub2, 0, offset)
                    : BinarySearchDesc(sub2, offset);
                if (idx2 >= 0) { FreeFrom2nd(sub2, idx2); return; }
            }

            Assert(false, $"Allocation to free not found (offset={offset}).");
        }

        private void FreeFrom1st(List<VmaSuballocation> sub1, int idx)
        {
            ulong freedSize = sub1[idx].Size;
            var freed = sub1[idx];
            freed.Type = VmaSuballocationType.Free;
            freed.UserData = null;
            sub1[idx] = freed;

            if (idx == m_1stNullItemsBeginCount)
            {
                // Advance the leading-null pointer, absorbing any existing middle-nulls.
                m_1stNullItemsBeginCount++;
                while (m_1stNullItemsBeginCount < sub1.Count
                    && sub1[m_1stNullItemsBeginCount].Type == VmaSuballocationType.Free)
                {
                    m_1stNullItemsBeginCount++;
                    if (m_1stNullItemsMiddleCount > 0) m_1stNullItemsMiddleCount--;
                }
            }
            else
            {
                m_1stNullItemsMiddleCount++;
            }

            // Compact the tail.
            while (sub1.Count > m_1stNullItemsBeginCount
                && sub1[sub1.Count - 1].Type == VmaSuballocationType.Free)
            {
                if (m_1stNullItemsMiddleCount > 0) m_1stNullItemsMiddleCount--;
                sub1.RemoveAt(sub1.Count - 1);
            }

            // If the 1st vector is now fully consumed in ring-buffer mode, promote
            // the 2nd vector to become the new 1st.
            if (sub1.Count == m_1stNullItemsBeginCount
                && m_2ndVectorMode == SecondVectorMode.RingBuffer)
            {
                sub1.Clear();
                m_1stNullItemsBeginCount = 0;
                m_1stVectorIndex ^= 1;
                m_2ndVectorMode = SecondVectorMode.Empty;
                m_2ndNullItemsCount = 0;
            }

            m_SumFreeSize += freedSize;
        }

        private void FreeFrom2nd(List<VmaSuballocation> sub2, int idx)
        {
            ulong freedSize = sub2[idx].Size;
            var freed = sub2[idx];
            freed.Type = VmaSuballocationType.Free;
            freed.UserData = null;
            sub2[idx] = freed;
            m_2ndNullItemsCount++;

            // Compact the tail.
            while (sub2.Count > 0 && sub2[sub2.Count - 1].Type == VmaSuballocationType.Free)
            {
                m_2ndNullItemsCount--;
                sub2.RemoveAt(sub2.Count - 1);
            }

            if (sub2.Count == 0 && m_2ndVectorMode == SecondVectorMode.DoubleStack)
                m_2ndVectorMode = SecondVectorMode.Empty;

            m_SumFreeSize += freedSize;
        }

        // ── UserData ─────────────────────────────────────────────────────────────

        public override object? GetAllocationUserData(ulong allocHandle) =>
            RequireSuballoc(allocHandle - 1).UserData;

        public override void SetAllocationUserData(ulong allocHandle, object? userData)
        {
            ulong offset = allocHandle - 1;
            var sub1 = GetSuballocations1st();
            int idx = BinarySearchAsc(sub1, m_1stNullItemsBeginCount, offset);
            if (idx >= 0) { var s = sub1[idx]; s.UserData = userData; sub1[idx] = s; return; }

            if (m_2ndVectorMode != SecondVectorMode.Empty)
            {
                var sub2 = GetSuballocations2nd();
                int idx2 = m_2ndVectorMode == SecondVectorMode.RingBuffer
                    ? BinarySearchAsc(sub2, 0, offset)
                    : BinarySearchDesc(sub2, offset);
                if (idx2 >= 0) { var s = sub2[idx2]; s.UserData = userData; sub2[idx2] = s; }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        // Binary search in a List that is sorted by ascending Offset.
        // Returns the index of the item with Offset == target, or -1.
        private static int BinarySearchAsc(List<VmaSuballocation> list, int startIdx, ulong target)
        {
            int lo = startIdx, hi = list.Count - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                ulong mo = list[mid].Offset;
                if (mo == target) return mid;
                if (mo < target) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        // Binary search in a List that is sorted by descending Offset (double-stack 2nd vector).
        private static int BinarySearchDesc(List<VmaSuballocation> list, ulong target)
        {
            int lo = 0, hi = list.Count - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                ulong mo = list[mid].Offset;
                if (mo == target) return mid;
                if (mo > target) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        private VmaSuballocation RequireSuballoc(ulong offset)
        {
            var sub1 = GetSuballocations1st();
            int idx = BinarySearchAsc(sub1, m_1stNullItemsBeginCount, offset);
            if (idx >= 0) return sub1[idx];

            if (m_2ndVectorMode != SecondVectorMode.Empty)
            {
                var sub2 = GetSuballocations2nd();
                int idx2 = m_2ndVectorMode == SecondVectorMode.RingBuffer
                    ? BinarySearchAsc(sub2, 0, offset)
                    : BinarySearchDesc(sub2, offset);
                if (idx2 >= 0) return sub2[idx2];
            }
            throw new InvalidOperationException($"Suballocation not found at offset {offset}.");
        }
    }
}
