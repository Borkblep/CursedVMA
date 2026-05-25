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

        // Owning allocator; null in standalone tests. Used for heap-size-limit
        // tracking, pNext chain building, and device-memory callback dispatch.
        private readonly VmaAllocator? m_Allocator;

        // Priority hint forwarded to VkMemoryPriorityAllocateInfoEXT when the
        // allocator was created with ExtMemoryPriorityBit.
        private readonly float m_Priority;

        // Debug guard margin in bytes; 0 disables corruption detection.
        private readonly ulong m_DebugMargin;

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
            nint pMemoryAllocateNext = 0,
            VmaAllocator? allocator = null,
            float priority = 0.5f,
            ulong debugMargin = 0)
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
            m_Allocator = allocator;
            m_Priority = priority;
            m_DebugMargin = debugMargin;
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

            // Heap-size-limit gate (no-op when allocator isn't tracking).
            if (m_Allocator != null
                && !m_Allocator.TryReserveHeapBytes(m_MemoryTypeIndex, blockSize))
                return Result.ErrorOutOfDeviceMemory;

            AllocationCallbacks ac = m_AllocationCallbacks.GetValueOrDefault();
            AllocationCallbacks* pAc = m_AllocationCallbacks.HasValue ? &ac : null;

            // Build pNext chain: MemoryAllocateFlagsInfo (device address) and
            // MemoryPriorityAllocateInfoEXT, each pointing to the next in line,
            // with the user's m_pMemoryAllocateNext at the tail.
            nint pNextChain = m_pMemoryAllocateNext;

            MemoryAllocateFlagsInfo flagsInfo = default;
            if (m_Allocator != null
                && (m_Allocator.Flags & VmaAllocatorCreateFlags.BufferDeviceAddressBit) != 0)
            {
                flagsInfo = new MemoryAllocateFlagsInfo
                {
                    SType  = StructureType.MemoryAllocateFlagsInfo,
                    PNext  = (void*)pNextChain,
                    Flags  = MemoryAllocateFlags.AddressBit,
                };
                pNextChain = (nint)(&flagsInfo);
            }

            MemoryPriorityAllocateInfoEXT priorityInfo = default;
            if (m_Allocator != null
                && (m_Allocator.Flags & VmaAllocatorCreateFlags.ExtMemoryPriorityBit) != 0)
            {
                priorityInfo = new MemoryPriorityAllocateInfoEXT
                {
                    SType    = StructureType.MemoryPriorityAllocateInfoExt,
                    PNext    = (void*)pNextChain,
                    Priority = m_Priority,
                };
                pNextChain = (nint)(&priorityInfo);
            }

            Result r = VmaDeviceMemoryBlock.Create(
                m_VkFunctions, m_Device, pAc,
                m_MemoryTypeIndex, blockSize, blockId,
                m_Algorithm, m_BufferImageGranularity,
                out VmaDeviceMemoryBlock? block,
                pNextChain,
                m_DebugMargin);

            if (r != Result.Success)
            {
                m_Allocator?.ReleaseHeapBytes(m_MemoryTypeIndex, blockSize);
                return r;
            }

            lock (m_MutexLock)
            {
                m_Blocks.Add(block!);
                newBlockIndex = m_Blocks.Count - 1;
            }
            m_Allocator?.NotifyDeviceMemoryAllocated(m_MemoryTypeIndex, block!.Memory, blockSize);
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
                        ulong size = m_Blocks[i].Metadata.GetSize();
                        DeviceMemory blockMemory = m_Blocks[i].Memory;
                        m_Allocator?.NotifyDeviceMemoryFreed(m_MemoryTypeIndex, blockMemory, size);
                        m_Blocks[i].Destroy(m_VkFunctions, m_Device, pAc);
                        m_Blocks.RemoveAt(i);
                        m_Allocator?.ReleaseHeapBytes(m_MemoryTypeIndex, size);
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
                {
                    ulong size = block.Metadata.GetSize();
                    DeviceMemory blockMemory = block.Memory;
                    m_Allocator?.NotifyDeviceMemoryFreed(m_MemoryTypeIndex, blockMemory, size);
                    block.Destroy(m_VkFunctions, m_Device, pAc);
                    m_Allocator?.ReleaseHeapBytes(m_MemoryTypeIndex, size);
                }
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
                ulong physicalOffset = block.Metadata.GetAllocationOffset(request.AllocHandle);
                ulong userOffset     = physicalOffset + m_DebugMargin;
                allocation = VmaAllocation.CreateBlockAllocation(
                    this, block, request.AllocHandle, userOffset, size, alignment, m_MemoryTypeIndex);
                // Store back-reference so defrag can recover the VmaAllocation from metadata.
                block.Metadata.SetAllocationUserData(request.AllocHandle, allocation);

                if (m_DebugMargin > 0)
                    block.WriteMagicValues(m_VkFunctions, m_Device, physicalOffset, size, m_DebugMargin);

                return true;
            }
            allocation = null;
            return false;
        }

        // ── Defragmentation helpers ───────────────────────────────────────────

        /// <summary>
        /// Tries to fit an allocation of <paramref name="size"/> /
        /// <paramref name="alignment"/> in any existing block other than
        /// <paramref name="excludeBlock"/>. When <paramref name="allowNewBlock"/>
        /// is true and all existing blocks fail, one new block is created and
        /// tried (used by the Extensive defrag algorithm). No new blocks are
        /// created when <paramref name="allowNewBlock"/> is false.
        /// Returns true and fills <paramref name="allocation"/> on success.
        /// </summary>
        internal bool TryAllocateForDefrag(
            ulong size,
            ulong alignment,
            VmaSuballocationType suballocType,
            VmaDeviceMemoryBlock excludeBlock,
            bool allowNewBlock,
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

            if (!allowNewBlock) return false;

            ulong minBlockForAlloc = size + (effectiveAlignment > 1 ? effectiveAlignment - 1 : 0);
            ulong newBlockSize = m_ExplicitBlockSize
                ? m_PreferredBlockSize
                : Math.Max(m_PreferredBlockSize, minBlockForAlloc);
            if (CreateBlock(newBlockSize, out _) != Result.Success)
                return false;

            lock (m_MutexLock)
            {
                // The newly created block lands at the end of the list.
                if (m_Blocks.Count > 0)
                {
                    var newest = m_Blocks[m_Blocks.Count - 1];
                    if (newest != excludeBlock && TryAllocFromBlock(
                            newest, size, effectiveAlignment,
                            upperAddress: false, suballocType, strategy: 0, out allocation))
                        return true;
                }
            }
            return false;
        }


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
            => TryAllocateForDefrag(size, alignment, suballocType, excludeBlock,
                allowNewBlock: false, out allocation);

        /// <summary>
        /// Frees <paramref name="allocHandle"/> from <paramref name="block"/>'s
        /// metadata. If the block is now empty and the vector is above its
        /// minimum count, destroys and removes the block and returns its size
        /// (in bytes); otherwise returns 0.
        /// </summary>
        internal unsafe ulong FreeAllocationFromBlock(
            VmaDeviceMemoryBlock block, ulong allocHandle)
        {
            DeviceMemory releasedBlockMemory = default;
            ulong releasedBlockSize = 0;
            lock (m_MutexLock)
            {
                block.Metadata.Free(allocHandle);

                if ((nuint)m_Blocks.Count > m_MinBlockCount && block.IsEmpty())
                {
                    AllocationCallbacks ac = m_AllocationCallbacks.GetValueOrDefault();
                    AllocationCallbacks* pAc = m_AllocationCallbacks.HasValue ? &ac : null;
                    releasedBlockSize  = block.Metadata.GetSize();
                    releasedBlockMemory = block.Memory;
                    m_Allocator?.NotifyDeviceMemoryFreed(m_MemoryTypeIndex, releasedBlockMemory, releasedBlockSize);
                    block.Destroy(m_VkFunctions, m_Device, pAc);
                    m_Blocks.Remove(block);
                }
            }
            if (releasedBlockSize > 0)
                m_Allocator?.ReleaseHeapBytes(m_MemoryTypeIndex, releasedBlockSize);
            return releasedBlockSize;
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
            DeviceMemory releasedBlockMemory = default;
            ulong releasedBlockSize = 0;
            lock (m_MutexLock)
            {
                block.Metadata.Free(allocation.AllocHandle);

                if ((nuint)m_Blocks.Count > m_MinBlockCount && block.IsEmpty())
                {
                    AllocationCallbacks ac = m_AllocationCallbacks.GetValueOrDefault();
                    AllocationCallbacks* pAc = m_AllocationCallbacks.HasValue ? &ac : null;
                    releasedBlockSize   = block.Metadata.GetSize();
                    releasedBlockMemory = block.Memory;
                    m_Allocator?.NotifyDeviceMemoryFreed(m_MemoryTypeIndex, releasedBlockMemory, releasedBlockSize);
                    block.Destroy(m_VkFunctions, m_Device, pAc);
                    m_Blocks.Remove(block);
                }
            }
            if (releasedBlockSize > 0)
                m_Allocator?.ReleaseHeapBytes(m_MemoryTypeIndex, releasedBlockSize);
        }

        /// <summary>
        /// Checks every block in the vector for debug-margin corruption. Returns
        /// <see cref="Result.ErrorFeatureNotPresent"/> when no debug margin is
        /// configured, <see cref="Result.Success"/> when all blocks are clean, or
        /// the first error code returned by an individual block check.
        /// Equivalent to <c>VmaBlockVector::CheckCorruption</c>.
        /// </summary>
        internal Result CheckCorruption()
        {
            if (m_DebugMargin == 0) return Result.ErrorFeatureNotPresent;

            lock (m_MutexLock)
            {
                foreach (var block in m_Blocks)
                {
                    Result r = block.CheckCorruption(m_VkFunctions, m_Device);
                    if (r != Result.Success && r != Result.ErrorFeatureNotPresent)
                        return r;
                }
            }
            return Result.Success;
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
