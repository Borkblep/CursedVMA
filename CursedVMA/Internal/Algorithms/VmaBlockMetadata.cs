// Mirrors the VmaBlockMetadata abstract base from vk_mem_alloc.cpp. Each
// concrete algorithm (TLSF in Phase 4, Linear in Phase 4) implements the
// abstract surface. The class is intentionally non-thread-safe; serialization
// is the caller's responsibility, matching VMA's contract.

using Silk.NET.Vulkan;
using System;

namespace CursedVMA.Internal.Algorithms
{
    /// <summary>
    /// Base class for the per-block allocation algorithm. A
    /// <see cref="VmaBlockMetadata"/> instance manages the suballocations
    /// within a single fixed-size chunk of memory (a <c>VkDeviceMemory</c>
    /// block for real allocations, or an abstract address space for a
    /// virtual block). All offsets and sizes are byte-addressed.
    /// </summary>
    internal abstract class VmaBlockMetadata
    {
        /// <summary>Vulkan buffer-image-granularity in bytes; 1 disables tracking.</summary>
        protected readonly ulong m_BufferImageGranularity;

        /// <summary>True for blocks belonging to a <see cref="VmaVirtualBlock"/>.</summary>
        protected readonly bool m_IsVirtual;

        /// <summary>Total size of the block in bytes; set by <see cref="Init"/>.</summary>
        protected ulong m_Size;

        /// <summary>Debug guard margin in bytes. When non-zero, magic sentinel bytes
        /// are written before and after every live suballocation so that
        /// <see cref="CheckCorruption"/> can detect out-of-bounds writes.</summary>
        protected readonly ulong m_DebugMargin;

        /// <summary>Magic byte pattern written into every debug-margin region.</summary>
        internal const byte DebugMagicByte = 0xEF;

        protected VmaBlockMetadata(ulong bufferImageGranularity, bool isVirtual, ulong debugMargin = 0)
        {
            m_BufferImageGranularity = bufferImageGranularity;
            m_IsVirtual = isVirtual;
            m_DebugMargin = debugMargin;
            m_Size = 0;
        }

        /// <summary>True when this metadata belongs to a virtual block.</summary>
        public bool IsVirtual() => m_IsVirtual;

        /// <summary>Block size in bytes.</summary>
        public ulong GetSize() => m_Size;

        /// <summary>Buffer-image-granularity this block enforces.</summary>
        public ulong GetBufferImageGranularity() => m_BufferImageGranularity;

        /// <summary>
        /// One-time setup of the block's storage. Concrete classes may grow
        /// internal structures here; the default implementation just records
        /// the size.
        /// </summary>
        public virtual void Init(ulong size)
        {
            m_Size = size;
        }

        /// <summary>Number of live (non-free) allocations.</summary>
        public abstract nuint GetAllocationCount();

        /// <summary>Number of contiguous free regions.</summary>
        public abstract nuint GetFreeRegionsCount();

        /// <summary>Sum, in bytes, of every free range.</summary>
        public abstract ulong GetSumFreeSize();

        /// <summary>Byte offset of an existing allocation within the block.</summary>
        public abstract ulong GetAllocationOffset(ulong allocHandle);

        /// <summary>True when no allocations are live.</summary>
        public abstract bool IsEmpty();

        /// <summary>Fills <paramref name="outInfo"/> with offset, size, and
        /// user data for an existing allocation.</summary>
        public abstract void GetAllocationInfo(ulong allocHandle, out VmaVirtualAllocationInfo outInfo);

        /// <summary>Handle of the first allocation in iteration order, or 0 if empty.</summary>
        public abstract ulong GetAllocationListBegin();

        /// <summary>Next allocation handle after <paramref name="prevAlloc"/>, or 0.</summary>
        public abstract ulong GetNextAllocation(ulong prevAlloc);

        /// <summary>Size, in bytes, of the free region that follows
        /// <paramref name="alloc"/>; 0 if the allocation is the last one.</summary>
        public abstract ulong GetNextFreeRegionSize(ulong alloc);

        /// <summary>Sanity-check the internal data structures; debug builds only.</summary>
        public abstract bool Validate();

        /// <summary>Accumulate detailed counters into <paramref name="stats"/>.</summary>
        public abstract void AddDetailedStatistics(ref VmaDetailedStatistics stats);

        /// <summary>Accumulate lightweight counters into <paramref name="stats"/>.</summary>
        public abstract void AddStatistics(ref VmaStatistics stats);

        /// <summary>
        /// Attempt to find a placement for an allocation of the given size and
        /// alignment. On success returns true and fills <paramref name="request"/>;
        /// commit the placement by passing the request to <see cref="Alloc"/>.
        /// </summary>
        public abstract bool CreateAllocationRequest(
            ulong allocSize,
            ulong allocAlignment,
            bool upperAddress,
            VmaSuballocationType allocType,
            uint strategy,
            out VmaAllocationRequest request);

        /// <summary>Commit a placement previously produced by
        /// <see cref="CreateAllocationRequest"/>.</summary>
        public abstract void Alloc(in VmaAllocationRequest request, VmaSuballocationType type, object? userData);

        /// <summary>Release an allocation by its handle.</summary>
        public abstract void Free(ulong allocHandle);

        /// <summary>Retrieve the user data attached to an allocation.</summary>
        public abstract object? GetAllocationUserData(ulong allocHandle);

        /// <summary>Replace the user data attached to an allocation.</summary>
        public abstract void SetAllocationUserData(ulong allocHandle, object? userData);

        /// <summary>Bulk-free every allocation in the block (used by
        /// <c>VmaVirtualBlock.Clear</c>).</summary>
        public abstract void Clear();

        /// <summary>
        /// Walks every live suballocation in the block and verifies the debug
        /// sentinel bytes that surround it. Returns
        /// <see cref="Result.Success"/> when all magic values are intact,
        /// <see cref="Result.ErrorUnknown"/> on the first corrupted region, and
        /// <see cref="Result.ErrorFeatureNotPresent"/> when
        /// <see cref="m_DebugMargin"/> is zero.
        /// </summary>
        public abstract unsafe Result CheckCorruption(byte* pBlockData);

        /// <summary>
        /// Throw if <paramref name="condition"/> is false. Concrete metadata
        /// implementations use this in <see cref="Validate"/> to surface
        /// corrupted internal state.
        /// </summary>
        protected static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>Fills <paramref name="count"/> bytes starting at
        /// <c>pData[offset]</c> with <see cref="DebugMagicByte"/>.</summary>
        protected static unsafe void WriteMagicValue(byte* pData, ulong offset, ulong count)
        {
            byte* p = pData + offset;
            for (ulong i = 0; i < count; i++) p[i] = DebugMagicByte;
        }

        /// <summary>Returns true when every byte in the range
        /// <c>pData[offset .. offset+count)</c> equals
        /// <see cref="DebugMagicByte"/>.</summary>
        protected static unsafe bool ValidateMagicValue(byte* pData, ulong offset, ulong count)
        {
            byte* p = pData + offset;
            for (ulong i = 0; i < count; i++)
                if (p[i] != DebugMagicByte) return false;
            return true;
        }
    }
}
