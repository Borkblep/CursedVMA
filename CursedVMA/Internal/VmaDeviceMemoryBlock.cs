// Ports VmaDeviceMemoryBlock from vk_mem_alloc.cpp. Wraps a single
// VkDeviceMemory allocation together with the block metadata algorithm that
// tracks its sub-allocations. Map/Unmap are reference-counted and guarded by
// a per-block lock, matching VMA's per-block mutex contract.

using CursedVMA.Internal.Algorithms;
using Silk.NET.Vulkan;
using System;
using System.Diagnostics;

namespace CursedVMA.Internal
{
    internal sealed class VmaDeviceMemoryBlock
    {
        private VmaBlockMetadata m_Metadata;
        private readonly uint m_MemoryTypeIndex;
        private readonly uint m_Id;
        private DeviceMemory m_Memory;
        private int m_MapCount;

        // Stored as nint to avoid requiring an unsafe field declaration.
        // Cast to void* at the call site when needed.
        private nint m_pMappedData;

        private readonly object m_MutexLock = new object();

        private VmaDeviceMemoryBlock(
            uint memoryTypeIndex,
            uint id,
            DeviceMemory memory,
            VmaBlockMetadata metadata)
        {
            m_MemoryTypeIndex = memoryTypeIndex;
            m_Id = id;
            m_Memory = memory;
            m_Metadata = metadata;
        }

        /// <summary>
        /// Allocates a new <c>VkDeviceMemory</c> object and initializes a block
        /// around it. Equivalent to calling <c>vkAllocateMemory</c> followed by
        /// <c>VmaDeviceMemoryBlock::Init</c> in the C++ port.
        /// </summary>
        internal static unsafe Result Create(
            IVulkanFunctions vkFunctions,
            Device device,
            AllocationCallbacks* pAllocationCallbacks,
            uint memoryTypeIndex,
            ulong size,
            uint id,
            VmaPoolCreateFlags algorithm,
            ulong bufferImageGranularity,
            out VmaDeviceMemoryBlock? block,
            nint pMemoryAllocateNext = 0)
        {
            block = null;
            if (size == 0)
                return Result.ErrorInitializationFailed;

            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = (void*)pMemoryAllocateNext,
                MemoryTypeIndex = memoryTypeIndex,
                AllocationSize = size,
            };

            Result r = vkFunctions.AllocateMemory(
                device, in allocInfo, pAllocationCallbacks, out DeviceMemory memory);
            if (r != Result.Success)
                return r;

            bool linear = (algorithm & VmaPoolCreateFlags.LinearAlgorithmBit) != 0;
            VmaBlockMetadata metadata = linear
                ? new VmaBlockMetadataLinear(bufferImageGranularity, isVirtual: false)
                : new VmaBlockMetadataTlsf(bufferImageGranularity, isVirtual: false);
            metadata.Init(size);

            block = new VmaDeviceMemoryBlock(memoryTypeIndex, id, memory, metadata);
            return Result.Success;
        }

        /// <summary>
        /// Frees the underlying <c>VkDeviceMemory</c>. Must be called before the
        /// block is abandoned; equivalent to <c>VmaDeviceMemoryBlock::Destroy</c>.
        /// </summary>
        internal unsafe void Destroy(
            IVulkanFunctions vkFunctions,
            Device device,
            AllocationCallbacks* pAllocationCallbacks)
        {
            if (m_Memory.Handle != 0ul)
            {
                vkFunctions.FreeMemory(device, m_Memory, pAllocationCallbacks);
                m_Memory = default;
            }
        }

        internal VmaBlockMetadata Metadata => m_Metadata;
        internal uint MemoryTypeIndex => m_MemoryTypeIndex;
        internal uint Id => m_Id;
        internal DeviceMemory Memory => m_Memory;

        /// <summary>Mapped host-visible pointer; only valid when <see cref="MapCount"/> > 0.</summary>
        internal unsafe void* MappedData => (void*)m_pMappedData;

        /// <summary>Current mapping reference count.</summary>
        internal int MapCount => m_MapCount;

        internal bool IsEmpty() => m_Metadata.IsEmpty();

        /// <summary>
        /// Reference-counted map. The first call with count > 0 invokes
        /// <c>vkMapMemory</c>; subsequent calls only increment the refcount.
        /// Equivalent to <c>VmaDeviceMemoryBlock::Map</c>.
        /// </summary>
        internal unsafe Result Map(
            IVulkanFunctions vkFunctions,
            Device device,
            uint count,
            out void* ppData)
        {
            ppData = null;
            if (count == 0)
                return Result.Success;

            lock (m_MutexLock)
            {
                if (m_MapCount > 0)
                {
                    m_MapCount += (int)count;
                    ppData = (void*)m_pMappedData;
                    return Result.Success;
                }

                Result r = vkFunctions.MapMemory(
                    device, m_Memory, 0, Vk.WholeSize, MemoryMapFlags.None, out void* p);
                if (r == Result.Success)
                {
                    m_pMappedData = (nint)p;
                    m_MapCount = (int)count;
                    ppData = p;
                }
                return r;
            }
        }

        /// <summary>
        /// Decrements the mapping refcount. When the count reaches zero,
        /// <c>vkUnmapMemory</c> is called. Equivalent to
        /// <c>VmaDeviceMemoryBlock::Unmap</c>.
        /// </summary>
        internal void Unmap(IVulkanFunctions vkFunctions, Device device, uint count)
        {
            if (count == 0)
                return;

            lock (m_MutexLock)
            {
                if (m_MapCount >= (int)count)
                {
                    m_MapCount -= (int)count;
                    if (m_MapCount == 0)
                    {
                        m_pMappedData = 0;
                        vkFunctions.UnmapMemory(device, m_Memory);
                    }
                }
                else
                {
                    Debug.Assert(false,
                        "VmaDeviceMemoryBlock unmapped more times than it was mapped.");
                }
            }
        }

        /// <summary>
        /// Binds a buffer to this block's memory. When <paramref name="pNext"/> is
        /// non-null the binding is submitted via <c>vkBindBufferMemory2</c>;
        /// otherwise <c>vkBindBufferMemory</c> is used. Equivalent to
        /// <c>VmaDeviceMemoryBlock::BindBufferMemory</c>.
        /// </summary>
        internal unsafe Result BindBufferMemory(
            IVulkanFunctions vkFunctions,
            Device device,
            ulong memoryOffset,
            Silk.NET.Vulkan.Buffer buffer,
            void* pNext)
        {
            lock (m_MutexLock)
            {
                if (pNext != null)
                {
                    var bindInfo = new BindBufferMemoryInfo
                    {
                        SType = StructureType.BindBufferMemoryInfo,
                        PNext = pNext,
                        Buffer = buffer,
                        Memory = m_Memory,
                        MemoryOffset = memoryOffset,
                    };
                    return vkFunctions.BindBufferMemory2(device, 1, &bindInfo);
                }
                return vkFunctions.BindBufferMemory(device, buffer, m_Memory, memoryOffset);
            }
        }

        /// <summary>
        /// Binds an image to this block's memory. When <paramref name="pNext"/> is
        /// non-null the binding is submitted via <c>vkBindImageMemory2</c>;
        /// otherwise <c>vkBindImageMemory</c> is used. Equivalent to
        /// <c>VmaDeviceMemoryBlock::BindImageMemory</c>.
        /// </summary>
        internal unsafe Result BindImageMemory(
            IVulkanFunctions vkFunctions,
            Device device,
            ulong memoryOffset,
            Image image,
            void* pNext)
        {
            lock (m_MutexLock)
            {
                if (pNext != null)
                {
                    var bindInfo = new BindImageMemoryInfo
                    {
                        SType = StructureType.BindImageMemoryInfo,
                        PNext = pNext,
                        Image = image,
                        Memory = m_Memory,
                        MemoryOffset = memoryOffset,
                    };
                    return vkFunctions.BindImageMemory2(device, 1, &bindInfo);
                }
                return vkFunctions.BindImageMemory(device, image, m_Memory, memoryOffset);
            }
        }
    }
}
