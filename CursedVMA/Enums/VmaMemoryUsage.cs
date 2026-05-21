// Mirrors VmaMemoryUsage from vk_mem_alloc.h.

namespace CursedVMA
{
    /// <summary>
    /// Intended usage of an allocation, used by VMA to choose a memory type when
    /// <see cref="VmaAllocationCreateInfo.MemoryTypeBits"/> alone does not pin one down.
    /// </summary>
    /// <remarks>
    /// The legacy "GPU_ONLY", "CPU_ONLY", "CPU_TO_GPU", "GPU_TO_CPU", "CPU_COPY"
    /// variants are still recognized for back-compat with VMA 2.x style code but
    /// new code should use one of the AUTO variants together with the
    /// HOST_ACCESS_* flags on <see cref="VmaAllocationCreateFlags"/>.
    /// </remarks>
    public enum VmaMemoryUsage : uint
    {
        /// <summary>No hint; VMA picks based on required/preferred memory property flags.</summary>
        Unknown = 0,

        /// <summary>(Legacy) Device-local memory, no host access expected.</summary>
        GpuOnly = 1,

        /// <summary>(Legacy) Host-visible memory; may not be coherent or cached.</summary>
        CpuOnly = 2,

        /// <summary>(Legacy) Host-visible, sequential-write-friendly (upload streaming).</summary>
        CpuToGpu = 3,

        /// <summary>(Legacy) Host-visible and cached (readback).</summary>
        GpuToCpu = 4,

        /// <summary>(Legacy) Memory used only for staging copies between host and device.</summary>
        CpuCopy = 5,

        /// <summary>Lazily allocated, transient attachment memory (mobile/tile architectures).</summary>
        GpuLazilyAllocated = 6,

        /// <summary>VMA selects the best memory type given the resource and host-access flags.</summary>
        Auto = 7,

        /// <summary>Like Auto but biased toward device-local memory types.</summary>
        AutoPreferDevice = 8,

        /// <summary>Like Auto but biased toward host-visible memory types.</summary>
        AutoPreferHost = 9,
    }
}
