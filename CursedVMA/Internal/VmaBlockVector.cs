// Ports VmaBlockVector from vk_mem_alloc.cpp. Manages a sorted list of
// VmaDeviceMemoryBlock objects all allocated from the same Vulkan memory type.
// Phase 7: lifecycle and statistics. Phase 9: AllocatePage / Free.

using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;

namespace CursedVMA.Internal
{
    internal sealed class VmaBlockVector
    {
        private readonly IVulkanFunctions m_VkFunctions;
        private readonly Device m_Device;
        private readonly AllocationCallbacks? m_AllocationCallbacks;

        private readonly uint m_MemoryTypeIndex;
        private readonly ulong m_PreferredBlockSize;
        private readonly nuint m_MinBlockCount;
        private readonly nuint m_MaxBlockCount;
        private readonly ulong m_BufferImageGranularity;
        private readonly VmaPoolCreateFlags m_Algorithm;
        private readonly bool m_ExplicitBlockSize;
        private readonly ulong m_MinAllocationAlignment;

        // Stored as nint (opaque pointer) to pass through to VkMemoryAllocateInfo.pNext.
        // The pointed-to memory must remain valid for the lifetime of the block vector.
        private readonly nint m_pMemoryAllocateNext;

        private readonly List<VmaDeviceMemoryBlock> m_Blocks = new List<VmaDeviceMemoryBlock>();
        private uint m_NextBlockId;
        private readonly object m_MutexLock = new object();

        internal VmaBlockVector(
            IVulkanFunctions vkFunctions,
            Device device,
            AllocationCallbacks? allocationCallbacks,
            uint memoryTypeIndex,
            ulong preferredBlockSize,
            nuint minBlockCount,
            nuint maxBlockCount,
            ulong bufferImageGranularity,
            VmaPoolCreateFlags algorithm,
            bool explicitBlockSize,
            ulong minAllocationAlignment,
            nint pMemoryAllocateNext = 0)
        {
            m_VkFunctions = vkFunctions;
            m_Device = device;
            m_AllocationCallbacks = allocationCallbacks;
            m_MemoryTypeIndex = memoryTypeIndex;
            m_PreferredBlockSize = preferredBlockSize;
            m_MinBlockCount = minBlockCount;
            m_MaxBlockCount = maxBlockCount;
            m_BufferImageGranularity = bufferImageGranularity;
            m_Algorithm = algorithm;
            m_ExplicitBlockSize = explicitBlockSize;
            m_MinAllocationAlignment = minAllocationAlignment;
            m_pMemoryAllocateNext = pMemoryAllocateNext;
        }

        internal uint MemoryTypeIndex => m_MemoryTypeIndex;
        internal ulong PreferredBlockSize => m_PreferredBlockSize;
        internal nuint MinBlockCount => m_MinBlockCount;
        internal nuint MaxBlockCount => m_MaxBlockCount;
        internal VmaPoolCreateFlags Algorithm => m_Algorithm;
        internal int BlockCount { get { lock (m_MutexLock) return m_Blocks.Count; } }

        /// <summary>
        /// Returns a snapshot of the current block list. Used by
        /// <c>VmaAllocator.BuildStatsString</c> for detailed dumps.
        /// </summary>
        internal VmaDeviceMemoryBlock[] GetBlockSnapshot()
        {
            lock (m_MutexLock)
                return m_Blocks.ToArray();
        }

        /// <summary>
        /// Pre-allocates <see cref="m_MinBlockCount"/> blocks. Called once after
        /// construction. Equivalent to <c>VmaBlockVector::CreateMinBlocks</c>.
        /// </summary>
        internal Result Init()
        {
            for (nuint i = 0; i < m_MinBlockCount; i++)
            {
                Result r = CreateBlock(m_PreferredBlockSize, out _);
                if (r != Result.Success)
                    return r;
            }
            return Result.Success;
        }

        /// <summary>
        /// Allocates a new <c>VkDeviceMemory</c> block of <paramref name="blockSize"/>
        /// bytes and appends it to the block list. Equivalent to
        /// <c>VmaBlockVector::CreateBlock</c>.
        /// </summary>
        internal unsafe Result CreateBlock(ulong blockSize, out int newBlockIndex)
        {
            newBlockIndex = -1;

            uint blockId;
            lock (m_MutexLock)
            {
                if (m_MaxBlockCount != nuint.MaxValue &&
                    (nuint)m_Blocks.Count >= m_MaxBlockCount)
                    return Result.ErrorOutOfDeviceMemory;
                blockId = m_NextBlockId++;
            }

            AllocationCallbacks ac = m_AllocationCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = m_AllocationCallbacks.HasValue ? &ac : null;

            Result r = VmaDeviceMemoryBlock.Create(
                m_VkFunctions, m_Device, pAc,
                m_MemoryTypeIndex, blockSize, blockId,
                m_Algorithm, m_BufferImageGranularity,
                out VmaDeviceMemoryBlock? block,
                m_pMemoryAllocateNext);

            if (r != Result.Success)
                return r;

            lock (m_MutexLock)
            {
                m_Blocks.Add(block!);
                newBlockIndex = m_Blocks.Count - 1;
            }
            return Result.Success;
        }

        /// <summary>
        /// Frees every block that holds no live sub-allocations, respecting
        /// <see cref="m_MinBlockCount"/>. Equivalent to
        /// <c>VmaBlockVector::FreeEmptyBlocks</c>.
        /// </summary>
        internal unsafe void FreeEmptyBlocks()
        {
            lock (m_MutexLock)
            {
                for (int i = m_Blocks.Count - 1; i >= 0; i--)
                {
                    if ((nuint)m_Blocks.Count <= m_MinBlockCount)
                        break;

                    if (m_Blocks[i].IsEmpty())
                    {
                        AllocationCallbacks ac = m_AllocationCallbacks.GetValueOrDefault();
                        AllocationCallbacks* pAc = m_AllocationCallbacks.HasValue ? &ac : null;
                        m_Blocks[i].Destroy(m_VkFunctions, m_Device, pAc);
                        m_Blocks.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// Tears down all blocks regardless of their emptiness. Called by
        /// <see cref="VmaAllocator.Dispose"/>. Equivalent to the destruction
        /// logic in <c>VmaBlockVector::~VmaBlockVector</c>.
        /// </summary>
        internal unsafe void Destroy()
        {
            lock (m_MutexLock)
            {
                AllocationCallbacks ac = m_AllocationCallbacks.GetValueOrDefault();
                AllocationCallbacks* pAc = m_AllocationCallbacks.HasValue ? &ac : null;

                foreach (var block in m_Blocks)
                    block.Destroy(m_VkFunctions, m_Device, pAc);
                m_Blocks.Clear();
            }
        }

        /// <summary>
        /// Finds a suballocation within an existing block or creates a new block to
        /// satisfy the request. Equivalent to <c>VmaBlockVector::AllocatePage</c>.
        /// </summary>
        /// <remarks>
        /// Dedicated allocations (<see cref="VmaAllocationCreateFlags.DedicatedMemoryBit"/>)
        /// are not handled here; that path lands in Phase 10.
        /// </remarks>
        internal Result AllocatePage(
            ulong size,
            ulong alignment,
            VmaAllocationCreateFlags flags,
            VmaSuballocationType suballocType,
            out VmaAllocation? allocation)
        {
            allocation = null;
            if (size == 0)
                return Result.ErrorInitializationFailed;

            bool neverAllocate = (flags & VmaAllocationCreateFlags.NeverAllocateBit) != 0;
            bool upperAddress  = (flags & VmaAllocationCreateFlags.UpperAddressBit)  != 0;
            uint strategy      = (uint)(flags & VmaAllocationCreateFlags.StrategyMask);
            ulong effectiveAlignment = Math.Max(alignment, m_MinAllocationAlignment);

            // Worst-case padding: an allocation of `size` may need up to
            // (effectiveAlignment - 1) extra bytes to reach its first aligned offset.
            ulong newBlockSize;
            lock (m_MutexLock)
            {
                foreach (var block in m_Blocks)
                {
                    if (TryAllocFromBlock(block, size, effectiveAlignment, upperAddress,
                            suballocType, strategy, out allocation))
                        return Result.Success;
                }

                if (neverAllocate)
                    return Result.ErrorOutOfDeviceMemory;

                ulong minBlockForAlloc = size + (effectiveAlignment > 1 ? effectiveAlignment - 1 : 0);
                newBlockSize = m_ExplicitBlockSize
                    ? m_PreferredBlockSize
                    : Math.Max(m_PreferredBlockSize, minBlockForAlloc);
            }

            // Create a new block outside the lock so vkAllocateMemory doesn't hold it.
            Result r = CreateBlock(newBlockSize, out _);
            if (r != Result.Success)
                return r;

            // Retry all blocks — the new one is now in the list.
            lock (m_MutexLock)
            {
                foreach (var block in m_Blocks)
                {
                    if (TryAllocFromBlock(block, size, effectiveAlignment, upperAddress,
                            suballocType, strategy, out allocation))
                        return Result.Success;
                }
                return Result.ErrorOutOfDeviceMemory;
            }
        }

        private bool TryAllocFromBlock(
            VmaDeviceMemoryBlock block,
            ulong size,
            ulong alignment,
            bool upperAddress,
            VmaSuballocationType suballocType,
            uint strategy,
            out VmaAllocation? allocation)
        {
            if (block.Metadata.CreateAllocationRequest(
                    size, alignment, upperAddress, suballocType, strategy, out var request))
            {
                block.Metadata.Alloc(in request, suballocType, null);
                ulong offset = block.Metadata.GetAllocationOffset(request.AllocHandle);
                allocation = VmaAllocation.CreateBlockAllocation(
                    this, block, request.AllocHandle, offset, size, alignment, m_MemoryTypeIndex);
                // Store back-reference so defrag can recover the VmaAllocation from metadata.
                block.Metadata.SetAllocationUserData(request.AllocHandle, allocation);
                return true;
            }
            allocation = null;
            return false;
        }

        // ── Defragmentation helpers ───────────────────────────────────────────

        /// <summary>
        /// Tries to fit an allocation of <paramref name="size"/> /
        /// <paramref name="alignment"/> in any existing block other than
        /// <paramref name="excludeBlock"/>. No new blocks are created.
        /// Returns true and fills <paramref name="allocation"/> on success.
        /// </summary>
        internal bool TryAllocateInExistingBlocks(
            ulong size,
            ulong alignment,
            VmaSuballocationType suballocType,
            VmaDeviceMemoryBlock excludeBlock,
            out VmaAllocation? allocation)
        {
            allocation = null;
            ulong effectiveAlignment = Math.Max(alignment, m_MinAllocationAlignment);
            lock (m_MutexLock)
            {
                foreach (var block in m_Blocks)
                {
                    if (block == excludeBlock) continue;
                    if (TryAllocFromBlock(block, size, effectiveAlignment,
                            upperAddress: false, suballocType, strategy: 0, out allocation))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Frees <paramref name="allocHandle"/> from <paramref name="block"/>'s
        /// metadata. If the block is now empty and the vector is above its
        /// minimum count, destroys and removes the block and returns its size
        /// (in bytes); otherwise returns 0.
        /// </summary>
        internal unsafe ulong FreeAllocationFromBlock(
            VmaDeviceMemoryBlock block, ulong allocHandle)
        {
            lock (m_MutexLock)
            {
                block.Metadata.Free(allocHandle);

                if ((nuint)m_Blocks.Count > m_MinBlockCount && block.IsEmpty())
                {
                    AllocationCallbacks ac = m_AllocationCallbacks.GetValueOrDefault();
                    AllocationCallbacks* pAc = m_AllocationCallbacks.HasValue ? &ac : null;
                    ulong blockSize = block.Metadata.GetSize();
                    block.Destroy(m_VkFunctions, m_Device, pAc);
                    m_Blocks.Remove(block);
                    return blockSize;
                }
                return 0;
            }
        }

        /// <summary>
        /// Updates the back-reference stored in <paramref name="block"/>'s
        /// metadata for <paramref name="handle"/> under the vector's lock.
        /// Used by defrag EndPass after a Copy swap.
        /// </summary>
        internal void UpdateAllocationUserData(
            VmaDeviceMemoryBlock block, ulong handle, object? userData)
        {
            lock (m_MutexLock)
                block.Metadata.SetAllocationUserData(handle, userData);
        }

        /// <summary>
        /// Returns a suballocation to its block and frees the block if it becomes
        /// empty and we are above the minimum block count. Equivalent to
        /// <c>VmaBlockVector::Free</c>.
        /// </summary>
        internal unsafe void Free(VmaAllocation allocation)
        {
            var block = allocation.Block;
            lock (m_MutexLock)
            {
                block.Metadata.Free(allocation.AllocHandle);

                if ((nuint)m_Blocks.Count > m_MinBlockCount && block.IsEmpty())
                {
                    AllocationCallbacks ac = m_AllocationCallbacks.GetValueOrDefault();
                    AllocationCallbacks* pAc = m_AllocationCallbacks.HasValue ? &ac : null;
                    block.Destroy(m_VkFunctions, m_Device, pAc);
                    m_Blocks.Remove(block);
                }
            }
        }

        /// <summary>
        /// Accumulates lightweight per-block statistics. Equivalent to
        /// <c>VmaBlockVector::AddStatistics</c>.
        /// </summary>
        internal void AddStatistics(ref VmaStatistics stats)
        {
            lock (m_MutexLock)
            {
                foreach (var block in m_Blocks)
                    block.Metadata.AddStatistics(ref stats);
            }
        }

        /// <summary>
        /// Accumulates detailed per-block statistics including free-range
        /// extrema. Equivalent to <c>VmaBlockVector::AddDetailedStatistics</c>.
        /// </summary>
        internal void AddDetailedStatistics(ref VmaDetailedStatistics stats)
        {
            lock (m_MutexLock)
            {
                foreach (var block in m_Blocks)
                    block.Metadata.AddDetailedStatistics(ref stats);
            }
        }
    }
}
