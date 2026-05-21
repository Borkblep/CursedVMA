// Mirrors VmaAllocationCreateFlagBits from vk_mem_alloc.h.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Flags for the <see cref="VmaAllocationCreateInfo.Flags"/> field, controlling
    /// per-allocation behavior such as host access mode, dedication, and the
    /// fitting strategy used when carving suballocations out of a block.
    /// </summary>
    [Flags]
    public enum VmaAllocationCreateFlags : uint
    {
        None = 0,

        /// <summary>Force a dedicated VkDeviceMemory object for this allocation.</summary>
        DedicatedMemoryBit = 0x00000001,

        /// <summary>Fail rather than allocating new device memory; only succeed if an
        /// existing block already has space.</summary>
        NeverAllocateBit = 0x00000002,

        /// <summary>Allocation should be persistently mapped on creation.</summary>
        MappedBit = 0x00000004,

        /// <summary>(Deprecated) UserData pointer points to a null-terminated C string
        /// that VMA copies internally; superseded by the <see cref="VmaAllocation"/>
        /// name API.</summary>
        UserDataCopyStringBit = 0x00000020,

        /// <summary>Allocate from the upper end of the block (only valid in linear-algorithm pools).</summary>
        UpperAddressBit = 0x00000040,

        /// <summary>Allocate device memory but do not bind it to the buffer or image;
        /// the caller is responsible for binding.</summary>
        DontBindBit = 0x00000080,

        /// <summary>Fail if the allocation would exceed the heap budget reported by
        /// VK_EXT_memory_budget.</summary>
        WithinBudgetBit = 0x00000100,

        /// <summary>Mark this allocation as one that may alias other allocations sharing
        /// the same memory range.</summary>
        CanAliasBit = 0x00000200,

        /// <summary>Host access pattern is sequential-write only (mapped pointer used as
        /// a write-combined upload buffer).</summary>
        HostAccessSequentialWriteBit = 0x00000400,

        /// <summary>Host access is random read/write (cached memory will be preferred).</summary>
        HostAccessRandomBit = 0x00000800,

        /// <summary>If the chosen memory type is not host-visible, allow VMA to allocate
        /// device-local memory instead and assume the caller will use a staging buffer.</summary>
        HostAccessAllowTransferInsteadBit = 0x00001000,

        /// <summary>Strategy: minimize memory waste (best-fit). Equivalent to
        /// StrategyBestFitBit.</summary>
        StrategyMinMemoryBit = 0x00010000,

        /// <summary>Strategy: minimize allocation time (first-fit). Equivalent to
        /// StrategyFirstFitBit.</summary>
        StrategyMinTimeBit = 0x00020000,

        /// <summary>Strategy: minimize the offset of the allocation within its block.</summary>
        StrategyMinOffsetBit = 0x00040000,

        /// <summary>Alias for <see cref="StrategyMinMemoryBit"/>.</summary>
        StrategyBestFitBit = StrategyMinMemoryBit,

        /// <summary>Alias for <see cref="StrategyMinTimeBit"/>.</summary>
        StrategyFirstFitBit = StrategyMinTimeBit,

        /// <summary>Mask covering all defined strategy bits.</summary>
        StrategyMask = StrategyMinMemoryBit | StrategyMinTimeBit | StrategyMinOffsetBit,
    }
}
