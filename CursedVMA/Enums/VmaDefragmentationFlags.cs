// Mirrors VmaDefragmentationFlagBits from vk_mem_alloc.h.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Flags for <see cref="VmaDefragmentationInfo.Flags"/>; primarily selects the
    /// defragmentation algorithm to apply.
    /// </summary>
    [Flags]
    public enum VmaDefragmentationFlags : uint
    {
        None = 0,

        /// <summary>Single-pass, low-cost. Some fragmentation may remain.</summary>
        AlgorithmFastBit = 0x1,

        /// <summary>Default. Balances time spent versus memory recovered.</summary>
        AlgorithmBalancedBit = 0x2,

        /// <summary>Exhaustive. Moves everything movable to maximize compaction.</summary>
        AlgorithmFullBit = 0x4,

        /// <summary>Full plus buffer-image-granularity-aware reordering. Highest quality, highest cost.</summary>
        AlgorithmExtensiveBit = 0x8,

        /// <summary>Mask covering all defined algorithm bits.</summary>
        AlgorithmMask =
            AlgorithmFastBit | AlgorithmBalancedBit | AlgorithmFullBit | AlgorithmExtensiveBit,
    }
}
