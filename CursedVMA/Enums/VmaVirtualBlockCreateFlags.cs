// Mirrors VmaVirtualBlockCreateFlagBits from vk_mem_alloc.h.

using System;

namespace CursedVMA
{
    /// <summary>
    /// Flags for <see cref="VmaVirtualBlockCreateInfo.Flags"/>, controlling the
    /// internal allocation algorithm used by a virtual block (which has no
    /// Vulkan dependency).
    /// </summary>
    [Flags]
    public enum VmaVirtualBlockCreateFlags : uint
    {
        None = 0,

        /// <summary>Use the linear (bump-pointer / double-stack) algorithm.</summary>
        LinearAlgorithmBit = 0x00000001,

        /// <summary>Mask covering every defined algorithm bit.</summary>
        AlgorithmMask = LinearAlgorithmBit,
    }
}
