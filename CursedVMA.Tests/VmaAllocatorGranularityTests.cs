// Phase 24c granularity tests at the allocator level: verifies that
// VmaPoolCreateFlags.IgnoreBufferImageGranularityBit collapses the
// pool's effective buffer-image granularity to 1, removing the padding
// VMA would otherwise insert between a buffer and an image suballocation.
//
// The allocator-level test is harder than the metadata-level one because
// VmaAllocator.AllocateMemory always passes VmaSuballocationType.Unknown,
// which is the granularity-conflict-with-anything type. We instead route
// through CreateBuffer + CreateImage so the right suballocation types are
// tagged on the suballocations.

using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaIgnoreBufferImageGranularityTests
    {
        private const ulong Granularity = 256;

        private static (FakeVulkanFunctions, VmaAllocator) MakeAllocator()
        {
            var fvk = new FakeVulkanFunctions();
            fvk.MemoryProperties.MemoryHeapCount = 1;
            fvk.MemoryProperties.MemoryTypeCount = 1;
            unsafe
            {
                fvk.MemoryProperties.MemoryHeaps[0] =
                    new MemoryHeap { Size = 64ul * 1024 * 1024 };
                fvk.MemoryProperties.MemoryTypes[0] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                };
            }
            // Force a known buffer-image granularity on the device.
            fvk.DeviceProperties = new PhysicalDeviceProperties
            {
                Limits = new PhysicalDeviceLimits
                {
                    BufferImageGranularity = Granularity,
                },
            };
            fvk.BufferMemoryRequirements = new MemoryRequirements
            { Size = 100, Alignment = 1, MemoryTypeBits = 1 };
            fvk.ImageMemoryRequirements = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };

            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
            };
            VmaAllocator.Create(fvk, ci, out var a);
            return (fvk, a!);
        }

        [Fact]
        public void WithoutIgnoreFlag_ImageAfterBuffer_OffsetIsGranularityAligned()
        {
            var (_, allocator) = MakeAllocator();

            var poolCi = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                BlockSize       = 16384,
                MinBlockCount   = 1,
                MaxBlockCount   = 1,
            };
            allocator.CreatePool(in poolCi, out var pool);

            // Allocate a buffer of 100 bytes through CreateBuffer so the
            // suballoc type is tagged as Buffer.
            var bufCI = new BufferCreateInfo
            { SType = StructureType.BufferCreateInfo, Size = 100 };
            var aci = new VmaAllocationCreateInfo { Pool = pool };
            allocator.CreateBuffer(in bufCI, in aci, out _, out var bufAlloc, out _);

            // Image after the buffer: must align to granularity.
            var imgCI = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            allocator.CreateImage(in imgCI, in aci, out _, out var imgAlloc, out _);

            allocator.GetAllocationInfo(imgAlloc!, out var imgInfo);
            Assert.Equal(0ul, imgInfo.Offset % Granularity);
            Assert.True(imgInfo.Offset >= Granularity,
                $"Image offset {imgInfo.Offset} not pushed past buffer end");

            allocator.FreeMemory(bufAlloc);
            allocator.FreeMemory(imgAlloc);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }

        [Fact]
        public void WithIgnoreFlag_ImageAfterBuffer_PacksTightlyNoPadding()
        {
            var (_, allocator) = MakeAllocator();

            var poolCi = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                BlockSize       = 16384,
                MinBlockCount   = 1,
                MaxBlockCount   = 1,
                Flags           = VmaPoolCreateFlags.IgnoreBufferImageGranularityBit,
            };
            allocator.CreatePool(in poolCi, out var pool);

            var bufCI = new BufferCreateInfo
            { SType = StructureType.BufferCreateInfo, Size = 100 };
            var aci = new VmaAllocationCreateInfo { Pool = pool };
            allocator.CreateBuffer(in bufCI, in aci, out _, out var bufAlloc, out _);

            var imgCI = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            allocator.CreateImage(in imgCI, in aci, out _, out var imgAlloc, out _);

            allocator.GetAllocationInfo(bufAlloc!, out var bufInfo);
            allocator.GetAllocationInfo(imgAlloc!, out var imgInfo);

            // With IgnoreBufferImageGranularity, the image lands immediately
            // after the buffer with no granularity padding inserted.
            Assert.Equal(bufInfo.Offset + 100, imgInfo.Offset);

            allocator.FreeMemory(bufAlloc);
            allocator.FreeMemory(imgAlloc);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }
    }
}
