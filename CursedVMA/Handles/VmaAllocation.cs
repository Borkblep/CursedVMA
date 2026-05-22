// Complete port of VmaAllocation_T from vk_mem_alloc.cpp for block-backed
// suballocations. Dedicated allocations (DedicatedMemoryBit) land in Phase 10.

using CursedVMA.Internal;
using Silk.NET.Vulkan;

namespace CursedVMA
{
    /// <summary>
    /// Opaque handle representing a single suballocation managed by VMA.
    /// Obtain via <see cref="VmaAllocator.AllocateMemory"/> and release with
    /// <see cref="VmaAllocator.FreeMemory"/>. Equivalent to the C++
    /// <c>VmaAllocation_T</c>.
    /// </summary>
    public sealed class VmaAllocation
    {
        // Block-backed allocation state.
        private VmaDeviceMemoryBlock m_Block = null!;
        private ulong m_AllocHandle; // handle within m_Block.Metadata (offset + 1)
        private VmaBlockVector m_OwningBlockVector = null!;

        // Cached allocation geometry.
        private ulong m_Offset;
        private ulong m_Size;
        private ulong m_Alignment;
        private uint m_MemoryTypeIndex;

        // User-visible metadata.
        private object? m_UserData;
        private string? m_Name;

        // Persistent mapping (reference-counted at allocation level; delegates to block).
        private int m_MapCount;
        private nint m_pMappedData; // = (byte*)block.MappedData + m_Offset

        private VmaAllocation() { }

        internal static VmaAllocation CreateBlockAllocation(
            VmaBlockVector owningBlockVector,
            VmaDeviceMemoryBlock block,
            ulong allocHandle,
            ulong offset,
            ulong size,
            ulong alignment,
            uint memoryTypeIndex)
        {
            var a = new VmaAllocation();
            a.m_OwningBlockVector = owningBlockVector;
            a.m_Block = block;
            a.m_AllocHandle = allocHandle;
            a.m_Offset = offset;
            a.m_Size = size;
            a.m_Alignment = alignment;
            a.m_MemoryTypeIndex = memoryTypeIndex;
            return a;
        }

        // ── Internal accessors ────────────────────────────────────────────────

        internal VmaBlockVector OwningBlockVector => m_OwningBlockVector;
        internal VmaDeviceMemoryBlock Block => m_Block;
        internal ulong AllocHandle => m_AllocHandle;
        internal int MapCount => m_MapCount;

        internal unsafe void* MappedData => (void*)m_pMappedData;

        internal unsafe void OnMapped(void* mappedBlockData)
        {
            m_MapCount++;
            if (m_pMappedData == 0)
                m_pMappedData = (nint)((byte*)mappedBlockData + m_Offset);
        }

        internal void OnUnmapped()
        {
            if (m_MapCount > 0)
            {
                m_MapCount--;
                if (m_MapCount == 0)
                    m_pMappedData = 0;
            }
        }

        // ── Public surface ────────────────────────────────────────────────────

        /// <summary>Index of the Vulkan memory type backing this allocation.</summary>
        public uint MemoryTypeIndex => m_MemoryTypeIndex;

        /// <summary>The underlying <c>VkDeviceMemory</c> object.</summary>
        public DeviceMemory Memory => m_Block.Memory;

        /// <summary>Byte offset of this allocation within <see cref="Memory"/>.</summary>
        public ulong Offset => m_Offset;

        /// <summary>Requested size of this allocation in bytes.</summary>
        public ulong Size => m_Size;

        /// <summary>Arbitrary user data. Assign via
        /// <see cref="VmaAllocator.SetAllocationUserData"/>.</summary>
        public object? UserData
        {
            get => m_UserData;
            internal set => m_UserData = value;
        }

        /// <summary>Debug name. Assign via
        /// <see cref="VmaAllocator.SetAllocationName"/>.</summary>
        public string? Name
        {
            get => m_Name;
            internal set => m_Name = value;
        }

        /// <summary>
        /// Fills <paramref name="info"/> with a snapshot of this allocation's
        /// current state. Equivalent to <c>vmaGetAllocationInfo</c>.
        /// </summary>
        public unsafe void GetInfo(out VmaAllocationInfo info)
        {
            info = new VmaAllocationInfo
            {
                MemoryType = m_MemoryTypeIndex,
                Memory = m_Block.Memory,
                Offset = m_Offset,
                Size = m_Size,
                MappedData = (void*)m_pMappedData,
                UserData = m_UserData,
                Name = m_Name,
            };
        }
    }
}
