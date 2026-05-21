// C# port of VmaAllocation_T from vk_mem_alloc.h. Phase 2 defines the type shell only.

namespace CursedVMA
{
    /// <summary>
    /// Opaque handle representing a single allocation managed by VMA. May refer
    /// to a suballocation within a block or a dedicated <c>VkDeviceMemory</c>
    /// object; both kinds share the same public surface. Equivalent to the
    /// C++ <c>VmaAllocation_T</c>.
    /// </summary>
    public sealed class VmaAllocation
    {
        internal VmaAllocation()
        {
        }
    }
}
