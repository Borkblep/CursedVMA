// Mirrors VmaPoolCreateFlagBits from vk_mem_alloc.h.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Flags for the <see cref="VmaPoolCreateInfo.Flags"/> field, selecting the
    /// internal allocation algorithm used within blocks owned by the pool and
    /// some granularity / alignment relaxations.
    /// </summary>
    [Flags]
    public enum VmaPoolCreateFlags : uint
    {
        None = 0,

        /// <summary>Disable VMA's buffer-image-granularity handling for this pool. Only
        /// safe if every allocation in the pool is the same image-tiling category
        /// (e.g. all buffers, or all optimal-tiling images).</summary>
        IgnoreBufferImageGranularityBit = 0x00000002,

        /// <summary>Use the linear (bump-pointer / double-stack) algorithm for blocks
        /// in this pool. Free order is restricted; see VMA documentation.</summary>
        LinearAlgorithmBit = 0x00000004,

        /// <summary>Mask covering every defined algorithm bit.</summary>
        AlgorithmMask = LinearAlgorithmBit,
    }
}
