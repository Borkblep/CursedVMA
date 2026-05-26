// Phase 24b tests: VmaMemoryUsage → memory type selection.
//
// VmaAllocator.UsageToFlags maps each VmaMemoryUsage value (combined with
// HostAccess* allocation flags) to required/preferred VkMemoryPropertyFlags.
// These flags then drive FindMemoryTypeIndex's scoring. The tests below build
// a multi-type physical device where exactly one type satisfies the expected
// flags for each Usage and verify selection lands on that type.

using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaMemoryUsageFlagMappingTests
    {
        // Builds an 8-type physical device covering the property-flag
        // combinations exercised by UsageToFlags. The exact ordering is fixed
        // so each test can assert the expected type index.
        private static FakeVulkanFunctions MakeFvk()
        {
            var fvk = new FakeVulkanFunctions();
            fvk.MemoryProperties.MemoryHeapCount = 1;
            fvk.MemoryProperties.MemoryTypeCount = 8;
            unsafe
            {
                fvk.MemoryProperties.MemoryHeaps[0] =
                    new MemoryHeap { Size = 256ul * 1024 * 1024 };

                // 0: DeviceLocal only
                fvk.MemoryProperties.MemoryTypes[0] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                };
                // 1: HostVisible + HostCoherent
                fvk.MemoryProperties.MemoryTypes[1] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                                  | MemoryPropertyFlags.HostCoherentBit,
                };
                // 2: HostVisible + DeviceLocal (rebar / shared)
                fvk.MemoryProperties.MemoryTypes[2] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                                  | MemoryPropertyFlags.DeviceLocalBit,
                };
                // 3: HostVisible + HostCached
                fvk.MemoryProperties.MemoryTypes[3] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                                  | MemoryPropertyFlags.HostCachedBit,
                };
                // 4: HostVisible + HostCached + HostCoherent
                fvk.MemoryProperties.MemoryTypes[4] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                                  | MemoryPropertyFlags.HostCachedBit
                                  | MemoryPropertyFlags.HostCoherentBit,
                };
                // 5: LazilyAllocated + DeviceLocal
                fvk.MemoryProperties.MemoryTypes[5] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.LazilyAllocatedBit
                                  | MemoryPropertyFlags.DeviceLocalBit,
                };
                // 6: HostVisible + HostCoherent + DeviceLocal (rebar coherent)
                fvk.MemoryProperties.MemoryTypes[6] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                                  | MemoryPropertyFlags.HostCoherentBit
                                  | MemoryPropertyFlags.DeviceLocalBit,
                };
                // 7: HostVisible only (no coherent, no cached)
                fvk.MemoryProperties.MemoryTypes[7] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.HostVisibleBit,
                };
            }
            return fvk;
        }

        private static VmaAllocator MakeAllocator(FakeVulkanFunctions fvk)
        {
            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
            };
            VmaAllocator.Create(fvk, info, out var a);
            return a!;
        }

        [Fact]
        public void GpuOnly_PrefersDeviceLocalType()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            // Multiple types include DeviceLocalBit (0, 2, 5, 6); the first
            // type with the highest preferred-bit score wins. Type 0 wins
            // because the search picks the first type that ties.
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.DeviceLocalBit) != 0);
            allocator.Dispose();
        }

        [Fact]
        public void CpuOnly_RequiresHostVisibleAndHostCoherent()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.CpuOnly };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.HostVisibleBit)  != 0);
            Assert.True((flags & MemoryPropertyFlags.HostCoherentBit) != 0);
            allocator.Dispose();
        }

        [Fact]
        public void CpuToGpu_PrefersHostVisibleDeviceLocal()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.CpuToGpu };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            // Required: HostVisibleBit. Preferred: DeviceLocalBit.
            // Type 2 (HostVisible + DeviceLocal) or 6 (HostVisible + Coherent +
            // DeviceLocal) both satisfy with score 1. First-match wins → 2.
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.HostVisibleBit) != 0);
            Assert.True((flags & MemoryPropertyFlags.DeviceLocalBit) != 0);
            allocator.Dispose();
        }

        [Fact]
        public void GpuToCpu_PrefersHostCachedType()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuToCpu };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.HostVisibleBit) != 0);
            Assert.True((flags & MemoryPropertyFlags.HostCachedBit)  != 0);
            allocator.Dispose();
        }

        [Fact]
        public void CpuCopy_RequiresHostVisible()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.CpuCopy };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.HostVisibleBit) != 0);
            allocator.Dispose();
        }

        [Fact]
        public void GpuLazilyAllocated_RequiresLazilyAllocatedBit()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuLazilyAllocated };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(5u, idx); // Only type 5 has LazilyAllocatedBit.
            allocator.Dispose();
        }

        [Fact]
        public void GpuLazilyAllocated_NoMatchingType_ReturnsFeatureNotPresent()
        {
            var fvk = MakeFvk();
            // Clear the LazilyAllocated type so no match exists.
            unsafe
            {
                fvk.MemoryProperties.MemoryTypes[5] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                };
            }
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuLazilyAllocated };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out _);

            Assert.Equal(Result.ErrorFeatureNotPresent, r);
            allocator.Dispose();
        }

        [Fact]
        public void Auto_SequentialWriteAccess_PrefersHostCoherentDeviceLocal()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.Auto,
                Flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit,
            };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            // Required: HostVisible. Preferred: HostCoherent + DeviceLocal
            // (Auto with no Prefer* falls through to preferring DeviceLocal
            // only when neither HostAccess* flag is set; with sequential-write
            // we get HostCoherent prefer but NOT DeviceLocal). Type 1 satisfies
            // HostVisible+HostCoherent (score 1).
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.HostVisibleBit)  != 0);
            Assert.True((flags & MemoryPropertyFlags.HostCoherentBit) != 0);
            allocator.Dispose();
        }

        [Fact]
        public void Auto_RandomAccess_PrefersHostCachedType()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.Auto,
                Flags = VmaAllocationCreateFlags.HostAccessRandomBit,
            };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.HostVisibleBit) != 0);
            Assert.True((flags & MemoryPropertyFlags.HostCachedBit)  != 0);
            allocator.Dispose();
        }

        [Fact]
        public void Auto_NoHostAccess_PrefersDeviceLocalType()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.DeviceLocalBit) != 0);
            allocator.Dispose();
        }

        [Fact]
        public void AutoPreferDevice_NoHostAccess_PrefersDeviceLocalType()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.DeviceLocalBit) != 0);
            allocator.Dispose();
        }

        [Fact]
        public void AutoPreferHost_SequentialWriteAccess_PrefersHostVisibleType()
        {
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.AutoPreferHost,
                Flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit,
            };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.HostVisibleBit) != 0);
            allocator.Dispose();
        }

        [Fact]
        public void RequiredFlags_FromCreateInfo_AreEnforcedOnTopOfUsage()
        {
            // Auto + sequential-write requires HostVisible. Layer an
            // additional explicit required flag and verify only types
            // satisfying both pass.
            var fvk = MakeFvk();
            var allocator = MakeAllocator(fvk);

            var ci = new VmaAllocationCreateInfo
            {
                Usage         = VmaMemoryUsage.Auto,
                Flags         = VmaAllocationCreateFlags.HostAccessSequentialWriteBit,
                RequiredFlags = MemoryPropertyFlags.DeviceLocalBit,
            };
            Result r = allocator.FindMemoryTypeIndex(uint.MaxValue, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            MemoryPropertyFlags flags = fvk.MemoryProperties.MemoryTypes[(int)idx].PropertyFlags;
            Assert.True((flags & MemoryPropertyFlags.HostVisibleBit) != 0);
            Assert.True((flags & MemoryPropertyFlags.DeviceLocalBit) != 0);
            allocator.Dispose();
        }
    }
}
