// Port of VmaAllocation_T from vk_mem_alloc.cpp. Phase 9 implemented the
// block-suballocation path. Phase 10 adds dedicated allocations where the
// VmaAllocation owns its own VkDeviceMemory rather than sharing a block.

using CursedVMA.Internal;
using Silk.NET.Vulkan;

namespace CursedVMA
{
    /// <summary>
    /// Opaque handle representing a single allocation managed by VMA. May refer
    /// to a suballocation within a shared <c>VkDeviceMemory</c> block (the
    /// common case) or to a dedicated <c>VkDeviceMemory</c> owned by this
    /// allocation. Equivalent to the C++ <c>VmaAllocation_T</c>.
    /// </summary>
    public sealed class VmaAllocation
    {
        // Discriminator: dedicated vs. block-backed.
        private bool m_IsDedicated;

        // Block-backed state (m_IsDedicated == false).
        private VmaDeviceMemoryBlock m_Block = null!;
        private ulong m_AllocHandle; // handle within m_Block.Metadata (offset + 1)
        private VmaBlockVector m_OwningBlockVector = null!;

        // Dedicated state (m_IsDedicated == true).
        private DeviceMemory m_DedicatedMemory;

        // Common geometry.
        private ulong m_Offset;        // 0 for dedicated, block-relative for suballoc
        private ulong m_Size;
        private ulong m_Alignment;
        private uint m_MemoryTypeIndex;

        // User-visible metadata.
        private object? m_UserData;
        private string? m_Name;

        // Mapping state. For block allocations m_pMappedData is the cached
        // (block.MappedData + offset). For dedicated it is the result of the
        // direct vkMapMemory call. Either way, m_MapCount is the per-allocation
        // reference count.
        private int m_MapCount;
        private nint m_pMappedData;

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
            a.m_IsDedicated = false;
            a.m_OwningBlockVector = owningBlockVector;
            a.m_Block = block;
            a.m_AllocHandle = allocHandle;
            a.m_Offset = offset;
            a.m_Size = size;
            a.m_Alignment = alignment;
            a.m_MemoryTypeIndex = memoryTypeIndex;
            return a;
        }

        internal static VmaAllocation CreateDedicatedAllocation(
            DeviceMemory memory,
            ulong size,
            ulong alignment,
            uint memoryTypeIndex)
        {
            var a = new VmaAllocation();
            a.m_IsDedicated = true;
            a.m_DedicatedMemory = memory;
            a.m_Offset = 0;
            a.m_Size = size;
            a.m_Alignment = alignment;
            a.m_MemoryTypeIndex = memoryTypeIndex;
            return a;
        }

        // ── Internal accessors ────────────────────────────────────────────────

        internal bool IsDedicated => m_IsDedicated;
        internal DeviceMemory DedicatedMemory => m_DedicatedMemory;

        internal VmaBlockVector OwningBlockVector => m_OwningBlockVector;
        internal VmaDeviceMemoryBlock Block => m_Block;
        internal ulong AllocHandle => m_AllocHandle;
        internal ulong Alignment => m_Alignment;
        internal int MapCount => m_MapCount;
        internal unsafe void* MappedData => (void*)m_pMappedData;

        // Defrag: repoint this allocation to a new block location. The caller
        // must capture the old block/handle BEFORE calling this, then free them.
        internal void SwapToBlock(
            VmaBlockVector newBv,
            VmaDeviceMemoryBlock newBlock,
            ulong newHandle,
            ulong newOffset)
        {
            m_OwningBlockVector = newBv;
            m_Block             = newBlock;
            m_AllocHandle       = newHandle;
            m_Offset            = newOffset;
            m_pMappedData       = 0;
        }

        /// <summary>
        /// Reference-counted map. Routes to <c>VmaDeviceMemoryBlock.Map</c> for
        /// block-backed allocations or to <c>vkMapMemory</c> directly for
        /// dedicated ones.
        /// </summary>
        internal unsafe Result Map(IVulkanFunctions vk, Device device, out void* ppData)
        {
            ppData = null;

            if (m_IsDedicated)
            {
                if (m_MapCount > 0)
                {
                    m_MapCount++;
                    ppData = (void*)m_pMappedData;
                    return Result.Success;
                }
                Result r = vk.MapMemory(
                    device, m_DedicatedMemory, 0, Vk.WholeSize,
                    MemoryMapFlags.None, out void* p);
                if (r != Result.Success) return r;
                m_pMappedData = (nint)p;
                m_MapCount = 1;
                ppData = p;
                return Result.Success;
            }

            Result br = m_Block.Map(vk, device, count: 1, out void* blockData);
            if (br != Result.Success) return br;
            m_MapCount++;
            if (m_pMappedData == 0)
                m_pMappedData = (nint)((byte*)blockData + m_Offset);
            ppData = (void*)m_pMappedData;
            return Result.Success;
        }

        /// <summary>Reference-counted unmap; mirror of <see cref="Map"/>.</summary>
        internal void Unmap(IVulkanFunctions vk, Device device)
        {
            if (m_IsDedicated)
            {
                if (m_MapCount > 0)
                {
                    m_MapCount--;
                    if (m_MapCount == 0)
                    {
                        m_pMappedData = 0;
                        vk.UnmapMemory(device, m_DedicatedMemory);
                    }
                }
                return;
            }

            if (m_MapCount > 0)
            {
                m_MapCount--;
                if (m_MapCount == 0)
                    m_pMappedData = 0;
            }
            m_Block.Unmap(vk, device, count: 1);
        }

        /// <summary>
        /// Binds <paramref name="buffer"/> to this allocation's memory. For block
        /// allocations the bind offset is <c>this.Offset + allocationLocalOffset</c>;
        /// for dedicated allocations it is <c>allocationLocalOffset</c> directly
        /// (the allocation owns the full <c>VkDeviceMemory</c>).
        /// </summary>
        internal unsafe Result BindBufferMemory(
            IVulkanFunctions vk, Device device,
            ulong allocationLocalOffset, Silk.NET.Vulkan.Buffer buffer, void* pNext)
        {
            if (m_IsDedicated)
            {
                if (pNext != null)
                {
                    var info = new BindBufferMemoryInfo
                    {
                        SType = StructureType.BindBufferMemoryInfo,
                        PNext = pNext,
                        Buffer = buffer,
                        Memory = m_DedicatedMemory,
                        MemoryOffset = allocationLocalOffset,
                    };
                    return vk.BindBufferMemory2(device, 1, &info);
                }
                return vk.BindBufferMemory(device, buffer, m_DedicatedMemory, allocationLocalOffset);
            }
            return m_Block.BindBufferMemory(
                vk, device, m_Offset + allocationLocalOffset, buffer, pNext);
        }

        /// <summary>Image analogue of <see cref="BindBufferMemory"/>.</summary>
        internal unsafe Result BindImageMemory(
            IVulkanFunctions vk, Device device,
            ulong allocationLocalOffset, Image image, void* pNext)
        {
            if (m_IsDedicated)
            {
                if (pNext != null)
                {
                    var info = new BindImageMemoryInfo
                    {
                        SType = StructureType.BindImageMemoryInfo,
                        PNext = pNext,
                        Image = image,
                        Memory = m_DedicatedMemory,
                        MemoryOffset = allocationLocalOffset,
                    };
                    return vk.BindImageMemory2(device, 1, &info);
                }
                return vk.BindImageMemory(device, image, m_DedicatedMemory, allocationLocalOffset);
            }
            return m_Block.BindImageMemory(
                vk, device, m_Offset + allocationLocalOffset, image, pNext);
        }

        // ── Public surface ────────────────────────────────────────────────────

        /// <summary>Index of the Vulkan memory type backing this allocation.</summary>
        public uint MemoryTypeIndex => m_MemoryTypeIndex;

        /// <summary>The underlying <c>VkDeviceMemory</c> object. For dedicated
        /// allocations this is unique; for block allocations it is shared with
        /// other suballocations in the same block.</summary>
        public DeviceMemory Memory => m_IsDedicated ? m_DedicatedMemory : m_Block.Memory;

        /// <summary>Byte offset of this allocation within <see cref="Memory"/>;
        /// always 0 for dedicated allocations.</summary>
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
                Memory = Memory,
                Offset = m_Offset,
                Size = m_Size,
                MappedData = (void*)m_pMappedData,
                UserData = m_UserData,
                Name = m_Name,
            };
        }
    }
}
