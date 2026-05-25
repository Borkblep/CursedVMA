// Phase 22 tests: VK_KHR_maintenance4 / Vulkan 1.3 wiring for
// FindMemoryTypeIndexForBufferInfo and FindMemoryTypeIndexForImageInfo.
//
// When KhrMaintenance4Bit, KhrMaintenance5Bit, or VulkanApiVersion >= 1.3 is set,
// these entry points should route to GetDeviceBufferMemoryRequirements /
// GetDeviceImageMemoryRequirements instead of creating and destroying a throwaway
// VkBuffer / VkImage just to query memory requirements.

using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    internal static class Phase22Fixture
    {
        internal static FakeVulkanFunctions MakeFvk()
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
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                                  | MemoryPropertyFlags.DeviceLocalBit,
                };
            }
            fvk.BufferMemoryRequirements = new MemoryRequirements
            {
                Size = 64, Alignment = 1, MemoryTypeBits = 1,
            };
            fvk.ImageMemoryRequirements = new MemoryRequirements
            {
                Size = 64, Alignment = 1, MemoryTypeBits = 1,
            };
            return fvk;
        }

        internal static VmaAllocator MakeAllocator(
            FakeVulkanFunctions fvk,
            VmaAllocatorCreateFlags flags = VmaAllocatorCreateFlags.None,
            uint vulkanApiVersion = 0)
        {
            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice   = new PhysicalDevice(1),
                Device           = new Device(1),
                Flags            = flags,
                VulkanApiVersion = vulkanApiVersion,
            };
            VmaAllocator.Create(fvk, info, out var a);
            return a!;
        }
    }

    // ── Buffer ─────────────────────────────────────────────────────────────────

    public sealed class VmaAllocatorMaintenance4BufferTests
    {
        [Fact]
        public void Maintenance4Bit_UsesDeviceBufferMemoryRequirements_NoProbe()
        {
            var fvk = Phase22Fixture.MakeFvk();
            var allocator = Phase22Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.KhrMaintenance4Bit);

            var bufInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo, Size = 64,
            };

            int createBefore  = fvk.CreateBufferCallCount;
            int destroyBefore = fvk.DestroyBufferCallCount;
            int deviceBefore  = fvk.GetDeviceBufferMemoryRequirementsCallCount;

            Result r = allocator.FindMemoryTypeIndexForBufferInfo(
                in bufInfo, new VmaAllocationCreateInfo(), out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, idx);
            Assert.Equal(createBefore,      fvk.CreateBufferCallCount);
            Assert.Equal(destroyBefore,     fvk.DestroyBufferCallCount);
            Assert.Equal(deviceBefore + 1,  fvk.GetDeviceBufferMemoryRequirementsCallCount);

            allocator.Dispose();
        }

        [Fact]
        public void Maintenance5Bit_AlsoUsesDeviceBufferMemoryRequirements()
        {
            var fvk = Phase22Fixture.MakeFvk();
            var allocator = Phase22Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.KhrMaintenance5Bit);

            var bufInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo, Size = 64,
            };

            int deviceBefore = fvk.GetDeviceBufferMemoryRequirementsCallCount;

            Result r = allocator.FindMemoryTypeIndexForBufferInfo(
                in bufInfo, new VmaAllocationCreateInfo(), out _);

            Assert.Equal(Result.Success, r);
            Assert.Equal(deviceBefore + 1, fvk.GetDeviceBufferMemoryRequirementsCallCount);

            allocator.Dispose();
        }

        [Fact]
        public void VulkanApi1_3_UsesDeviceBufferMemoryRequirements()
        {
            // VK_API_VERSION_1_3 == (1 << 22) | (3 << 12)
            const uint apiVersion1_3 = (1u << 22) | (3u << 12);
            var fvk = Phase22Fixture.MakeFvk();
            var allocator = Phase22Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.None, apiVersion1_3);

            var bufInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo, Size = 64,
            };

            int createBefore = fvk.CreateBufferCallCount;
            int deviceBefore = fvk.GetDeviceBufferMemoryRequirementsCallCount;

            Result r = allocator.FindMemoryTypeIndexForBufferInfo(
                in bufInfo, new VmaAllocationCreateInfo(), out _);

            Assert.Equal(Result.Success, r);
            Assert.Equal(createBefore,     fvk.CreateBufferCallCount);
            Assert.Equal(deviceBefore + 1, fvk.GetDeviceBufferMemoryRequirementsCallCount);

            allocator.Dispose();
        }

        [Fact]
        public void NoFlag_NoApi1_3_FallsBackToProbeCreateDestroy()
        {
            // VK_API_VERSION_1_2 == (1 << 22) | (2 << 12); below 1.3.
            const uint apiVersion1_2 = (1u << 22) | (2u << 12);
            var fvk = Phase22Fixture.MakeFvk();
            var allocator = Phase22Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.None, apiVersion1_2);

            var bufInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo, Size = 64,
            };

            int createBefore  = fvk.CreateBufferCallCount;
            int destroyBefore = fvk.DestroyBufferCallCount;
            int deviceBefore  = fvk.GetDeviceBufferMemoryRequirementsCallCount;

            Result r = allocator.FindMemoryTypeIndexForBufferInfo(
                in bufInfo, new VmaAllocationCreateInfo(), out _);

            Assert.Equal(Result.Success, r);
            Assert.Equal(createBefore + 1,  fvk.CreateBufferCallCount);
            Assert.Equal(destroyBefore + 1, fvk.DestroyBufferCallCount);
            Assert.Equal(deviceBefore,      fvk.GetDeviceBufferMemoryRequirementsCallCount);

            allocator.Dispose();
        }
    }

    // ── Image ──────────────────────────────────────────────────────────────────

    public sealed class VmaAllocatorMaintenance4ImageTests
    {
        [Fact]
        public void Maintenance4Bit_UsesDeviceImageMemoryRequirements_NoProbe()
        {
            var fvk = Phase22Fixture.MakeFvk();
            var allocator = Phase22Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.KhrMaintenance4Bit);

            var imgInfo = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };

            int createBefore  = fvk.CreateImageCallCount;
            int destroyBefore = fvk.DestroyImageCallCount;
            int deviceBefore  = fvk.GetDeviceImageMemoryRequirementsCallCount;

            Result r = allocator.FindMemoryTypeIndexForImageInfo(
                in imgInfo, new VmaAllocationCreateInfo(), out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, idx);
            Assert.Equal(createBefore,      fvk.CreateImageCallCount);
            Assert.Equal(destroyBefore,     fvk.DestroyImageCallCount);
            Assert.Equal(deviceBefore + 1,  fvk.GetDeviceImageMemoryRequirementsCallCount);

            allocator.Dispose();
        }

        [Fact]
        public void Maintenance5Bit_AlsoUsesDeviceImageMemoryRequirements()
        {
            var fvk = Phase22Fixture.MakeFvk();
            var allocator = Phase22Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.KhrMaintenance5Bit);

            var imgInfo = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };

            int deviceBefore = fvk.GetDeviceImageMemoryRequirementsCallCount;

            Result r = allocator.FindMemoryTypeIndexForImageInfo(
                in imgInfo, new VmaAllocationCreateInfo(), out _);

            Assert.Equal(Result.Success, r);
            Assert.Equal(deviceBefore + 1, fvk.GetDeviceImageMemoryRequirementsCallCount);

            allocator.Dispose();
        }

        [Fact]
        public void VulkanApi1_3_UsesDeviceImageMemoryRequirements()
        {
            const uint apiVersion1_3 = (1u << 22) | (3u << 12);
            var fvk = Phase22Fixture.MakeFvk();
            var allocator = Phase22Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.None, apiVersion1_3);

            var imgInfo = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };

            int createBefore = fvk.CreateImageCallCount;
            int deviceBefore = fvk.GetDeviceImageMemoryRequirementsCallCount;

            Result r = allocator.FindMemoryTypeIndexForImageInfo(
                in imgInfo, new VmaAllocationCreateInfo(), out _);

            Assert.Equal(Result.Success, r);
            Assert.Equal(createBefore,     fvk.CreateImageCallCount);
            Assert.Equal(deviceBefore + 1, fvk.GetDeviceImageMemoryRequirementsCallCount);

            allocator.Dispose();
        }

        [Fact]
        public void NoFlag_NoApi1_3_FallsBackToProbeCreateDestroy()
        {
            const uint apiVersion1_2 = (1u << 22) | (2u << 12);
            var fvk = Phase22Fixture.MakeFvk();
            var allocator = Phase22Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.None, apiVersion1_2);

            var imgInfo = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };

            int createBefore  = fvk.CreateImageCallCount;
            int destroyBefore = fvk.DestroyImageCallCount;
            int deviceBefore  = fvk.GetDeviceImageMemoryRequirementsCallCount;

            Result r = allocator.FindMemoryTypeIndexForImageInfo(
                in imgInfo, new VmaAllocationCreateInfo(), out _);

            Assert.Equal(Result.Success, r);
            Assert.Equal(createBefore + 1,  fvk.CreateImageCallCount);
            Assert.Equal(destroyBefore + 1, fvk.DestroyImageCallCount);
            Assert.Equal(deviceBefore,      fvk.GetDeviceImageMemoryRequirementsCallCount);

            allocator.Dispose();
        }
    }

    // ── Returned data ──────────────────────────────────────────────────────────

    public sealed class VmaAllocatorMaintenance4DataTests
    {
        [Fact]
        public void Buffer_ReturnedMemoryTypeIndex_MatchesRequirements()
        {
            var fvk = Phase22Fixture.MakeFvk();
            // Only memory-type bit 0 is allowed.
            fvk.BufferMemoryRequirements = new MemoryRequirements
            {
                Size = 256, Alignment = 4, MemoryTypeBits = 1,
            };
            var allocator = Phase22Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.KhrMaintenance4Bit);

            var bufInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo, Size = 256,
            };
            Result r = allocator.FindMemoryTypeIndexForBufferInfo(
                in bufInfo, new VmaAllocationCreateInfo(), out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, idx);
            allocator.Dispose();
        }

        [Fact]
        public void Image_ReturnedMemoryTypeIndex_MatchesRequirements()
        {
            var fvk = Phase22Fixture.MakeFvk();
            fvk.ImageMemoryRequirements = new MemoryRequirements
            {
                Size = 1024, Alignment = 16, MemoryTypeBits = 1,
            };
            var allocator = Phase22Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.KhrMaintenance4Bit);

            var imgInfo = new ImageCreateInfo { SType = StructureType.ImageCreateInfo };
            Result r = allocator.FindMemoryTypeIndexForImageInfo(
                in imgInfo, new VmaAllocationCreateInfo(), out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, idx);
            allocator.Dispose();
        }
    }
}
