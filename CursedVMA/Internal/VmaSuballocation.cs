// Mirrors VmaSuballocation from vk_mem_alloc.cpp.

namespace CursedVMA.Internal
{
    /// <summary>
    /// A single suballocation record within a block: where it lives, how big
    /// it is, what category of resource it backs, and any caller-supplied
    /// user data. Used by both the TLSF and Linear block metadata
    /// implementations to describe their stored allocations.
    /// </summary>
    internal struct VmaSuballocation
    {
        /// <summary>Byte offset within the parent block.</summary>
        public ulong Offset;

        /// <summary>Size, in bytes, of this suballocation.</summary>
        public ulong Size;

        /// <summary>User data attached to the suballocation; null for free regions.</summary>
        public object? UserData;

        /// <summary>Resource category, used for buffer-image-granularity decisions.</summary>
        public VmaSuballocationType Type;
    }
}
