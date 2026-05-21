// Mirrors VmaVirtualAllocationCreateFlagBits from vk_mem_alloc.h.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Flags for <see cref="VmaVirtualAllocationCreateInfo.Flags"/>; mirrors the
    /// equivalent VmaAllocationCreateFlags bits so the same fitting strategies
    /// and upper-address behavior apply within virtual blocks.
    /// </summary>
    [Flags]
    public enum VmaVirtualAllocationCreateFlags : uint
    {
        None = 0,

        /// <summary>Allocate from the upper end of the block; only valid in linear-algorithm blocks.</summary>
        UpperAddressBit = VmaAllocationCreateFlags.UpperAddressBit,

        /// <summary>Strategy: minimize memory waste (best-fit).</summary>
        StrategyMinMemoryBit = VmaAllocationCreateFlags.StrategyMinMemoryBit,

        /// <summary>Strategy: minimize allocation time (first-fit).</summary>
        StrategyMinTimeBit = VmaAllocationCreateFlags.StrategyMinTimeBit,

        /// <summary>Strategy: minimize the offset of the allocation within its block.</summary>
        StrategyMinOffsetBit = VmaAllocationCreateFlags.StrategyMinOffsetBit,

        /// <summary>Mask covering every defined strategy bit.</summary>
        StrategyMask = VmaAllocationCreateFlags.StrategyMask,
    }
}
