// Mirrors VmaSuballocationType from vk_mem_alloc.cpp. Internal because no
// caller of the managed port should ever need to spell these out by hand;
// they are inferred from the public allocation entry point chosen (Buffer
// vs. Image, with image-tiling categorized by VkImageTiling).

namespace CursedVMA.Internal
{
    /// <summary>
    /// Category of the resource an allocation backs, used by the block
    /// metadata to enforce Vulkan's buffer-image-granularity constraint when
    /// two adjacent suballocations have incompatible tiling.
    /// </summary>
    /// <remarks>
    /// Values match the C++ <c>VmaSuballocationType</c> enum exactly; the
    /// numeric ordering matters because
    /// <see cref="VmaMath.IsBufferImageGranularityConflict"/> compares pairs
    /// with the smaller value first.
    /// </remarks>
    internal enum VmaSuballocationType : byte
    {
        /// <summary>Unallocated (free) range; no granularity tracking needed.</summary>
        Free = 0,

        /// <summary>Resource category is not known; conservatively assumed to
        /// conflict with every non-free neighbor.</summary>
        Unknown = 1,

        /// <summary>VkBuffer (always linearly tiled).</summary>
        Buffer = 2,

        /// <summary>VkImage of unknown tiling; conservatively conflicts with
        /// every other image-class neighbor.</summary>
        ImageUnknown = 3,

        /// <summary>VkImage with <c>VK_IMAGE_TILING_LINEAR</c>.</summary>
        ImageLinear = 4,

        /// <summary>VkImage with <c>VK_IMAGE_TILING_OPTIMAL</c>.</summary>
        ImageOptimal = 5,
    }
}
