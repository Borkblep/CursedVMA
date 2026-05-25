// Phase 23 tests: AmdDeviceCoherentMemoryBit filtering and
// TypeExternalMemoryHandleTypes pNext threading.
//
// When AmdDeviceCoherentMemoryBit is NOT set on the allocator, memory types
// carrying VK_MEMORY_PROPERTY_DEVICE_COHERENT_BIT_AMD are excluded from every
// FindMemoryTypeIndex selection (mirrors VMA's m_GlobalMemoryTypeBits).
//
// When TypeExternalMemoryHandleTypes[i] is non-zero, allocations from memory
// type i include a VkExportMemoryAllocateInfo entry in their pNext chain.

using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    // ── Shared fixture ──────────────────────────────────────────────────────

    internal static class Phase23Fixture
    {
        internal static FakeVulkanFunctions MakeFvk(
            MemoryPropertyFlags type0Flags = MemoryPropertyFlags.DeviceLocalBit,
            MemoryPropertyFlags type1Flags = MemoryPropertyFlags.DeviceLocalBit
                                          | MemoryPropertyFlags.DeviceCoherentBitAmd)
        {
            var fvk = new FakeVulkanFunctions();
            fvk.MemoryProperties.MemoryHeapCount = 1;
            fvk.MemoryProperties.MemoryTypeCount = 2;
            unsafe
            {
                fvk.MemoryProperties.MemoryHeaps[0] =
                    new MemoryHeap { Size = 64ul * 1024 * 1024 };
                fvk.MemoryProperties.MemoryTypes[0] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = type0Flags,
                };
                fvk.MemoryProperties.MemoryTypes[1] = new MemoryType
                {
                    HeapIndex     = 0,
                    PropertyFlags = type1Flags,
                };
            }
            return fvk;
        }

        internal static VmaAllocator MakeAllocator(
            FakeVulkanFunctions fvk,
            VmaAllocatorCreateFlags flags = VmaAllocatorCreateFlags.None,
            ExternalMemoryHandleTypeFlags[]? handleTypes = null)
        {
            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice                = new PhysicalDevice(1),
                Device                        = new Device(1),
                Flags                         = flags,
                TypeExternalMemoryHandleTypes = handleTypes,
            };
            VmaAllocator.Create(fvk, info, out var a);
            return a!;
        }
    }

    // ── AMD device-coherent filtering ──────────────────────────────────────

    public sealed class VmaAmdDeviceCoherentFilterTests
    {
        [Fact]
        public void WithoutFlag_CoherentTypeExcludedFromSelection()
        {
            var fvk = Phase23Fixture.MakeFvk();
            var allocator = Phase23Fixture.MakeAllocator(fvk);

            // memoryTypeBits = 0b11 (both types acceptable). Without the AMD flag,
            // type 1 (DEVICE_COHERENT_BIT_AMD) must be filtered out.
            Result r = allocator.FindMemoryTypeIndex(
                0b11u, new VmaAllocationCreateInfo(), out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, idx);
            allocator.Dispose();
        }

        [Fact]
        public void WithFlag_CoherentTypeEligibleForSelection()
        {
            var fvk = Phase23Fixture.MakeFvk();
            var allocator = Phase23Fixture.MakeAllocator(
                fvk, VmaAllocatorCreateFlags.AmdDeviceCoherentMemoryBit);

            // Request requires DEVICE_COHERENT_BIT_AMD; only type 1 satisfies it.
            // Without the AMD flag this would fail (filtered out); with it, succeeds.
            var ci = new VmaAllocationCreateInfo
            {
                RequiredFlags = MemoryPropertyFlags.DeviceCoherentBitAmd,
            };
            Result r = allocator.FindMemoryTypeIndex(0b11u, in ci, out uint idx);

            Assert.Equal(Result.Success, r);
            Assert.Equal(1u, idx);
            allocator.Dispose();
        }

        [Fact]
        public void WithoutFlag_CoherentOnlyRequest_FailsWithFeatureNotPresent()
        {
            var fvk = Phase23Fixture.MakeFvk();
            var allocator = Phase23Fixture.MakeAllocator(fvk);

            // Caller explicitly demands the AMD bit but the allocator wasn't
            // created with the AMD coherent flag, so no candidates remain.
            var ci = new VmaAllocationCreateInfo
            {
                RequiredFlags = MemoryPropertyFlags.DeviceCoherentBitAmd,
            };
            Result r = allocator.FindMemoryTypeIndex(0b11u, in ci, out uint idx);

            Assert.Equal(Result.ErrorFeatureNotPresent, r);
            Assert.Equal(uint.MaxValue, idx);
            allocator.Dispose();
        }

        [Fact]
        public void NoCoherentTypesPresent_FlagHasNoEffect()
        {
            var fvk = Phase23Fixture.MakeFvk(
                type0Flags: MemoryPropertyFlags.DeviceLocalBit,
                type1Flags: MemoryPropertyFlags.HostVisibleBit);
            var allocator = Phase23Fixture.MakeAllocator(fvk);

            Result r = allocator.FindMemoryTypeIndex(
                0b11u, new VmaAllocationCreateInfo(), out uint idx);

            Assert.Equal(Result.Success, r);
            // Both types remain candidates; type 0 (device-local) wins by default.
            Assert.Equal(0u, idx);
            allocator.Dispose();
        }
    }

    // ── External-memory pNext threading ────────────────────────────────────

    public sealed class VmaExternalMemoryHandleTypesTests
    {
        [Fact]
        public void HandleTypesSet_DedicatedAllocationChainsExportInfo()
        {
            var fvk = Phase23Fixture.MakeFvk();
            // Only type 0 gets a handle type; allocation from type 0 should
            // include ExportMemoryAllocateInfo. We allow type 0 by setting
            // AmdDeviceCoherentMemoryBit so both types are candidates and the
            // dedicated alloc lands in type 0 (its required flags are
            // DeviceLocalBit only).
            var handleTypes = new ExternalMemoryHandleTypeFlags[]
            {
                ExternalMemoryHandleTypeFlags.OpaqueFDBit,
                0,
            };
            var allocator = Phase23Fixture.MakeAllocator(
                fvk, handleTypes: handleTypes);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 0b01 };
            var ci  = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };

            Result r = allocator.AllocateMemory(in req, in ci, out var alloc);
            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, fvk.LastAllocateMemoryTypeIndex);
            Assert.Equal(ExternalMemoryHandleTypeFlags.OpaqueFDBit,
                fvk.LastAllocateExportHandleTypes);

            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }

        [Fact]
        public void HandleTypeZeroForSlot_NoExportInfoChained()
        {
            var fvk = Phase23Fixture.MakeFvk();
            // Type 0 has no external handle; type 1 does. We allocate from type 0
            // and expect no ExportMemoryAllocateInfo.
            var handleTypes = new ExternalMemoryHandleTypeFlags[]
            {
                0,
                ExternalMemoryHandleTypeFlags.OpaqueFDBit,
            };
            var allocator = Phase23Fixture.MakeAllocator(
                fvk, handleTypes: handleTypes);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 0b01 };
            var ci  = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };

            Result r = allocator.AllocateMemory(in req, in ci, out var alloc);
            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, fvk.LastAllocateMemoryTypeIndex);
            Assert.Equal((ExternalMemoryHandleTypeFlags)0,
                fvk.LastAllocateExportHandleTypes);

            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }

        [Fact]
        public void NoArray_NoExportInfoChained()
        {
            var fvk = Phase23Fixture.MakeFvk();
            var allocator = Phase23Fixture.MakeAllocator(fvk);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 0b01 };
            var ci  = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };

            allocator.AllocateMemory(in req, in ci, out var alloc);
            Assert.Equal((ExternalMemoryHandleTypeFlags)0,
                fvk.LastAllocateExportHandleTypes);

            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }

        [Fact]
        public void HandleTypesSet_BlockAllocationChainsExportInfo()
        {
            var fvk = Phase23Fixture.MakeFvk();
            var handleTypes = new ExternalMemoryHandleTypeFlags[]
            {
                ExternalMemoryHandleTypeFlags.OpaqueFDBit,
                0,
            };
            var allocator = Phase23Fixture.MakeAllocator(
                fvk, handleTypes: handleTypes);

            var req = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 0b01 };
            var ci  = new VmaAllocationCreateInfo();

            // Triggers VmaBlockVector.CreateBlock which calls AllocateMemory.
            Result r = allocator.AllocateMemory(in req, in ci, out var alloc);
            Assert.Equal(Result.Success, r);
            Assert.Equal(0u, fvk.LastAllocateMemoryTypeIndex);
            Assert.Equal(ExternalMemoryHandleTypeFlags.OpaqueFDBit,
                fvk.LastAllocateExportHandleTypes);

            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }

        [Fact]
        public void HandleTypesSet_ChainsAlongsideOtherPNextEntries()
        {
            var fvk = Phase23Fixture.MakeFvk();
            var handleTypes = new ExternalMemoryHandleTypeFlags[]
            {
                ExternalMemoryHandleTypeFlags.OpaqueFDBit,
                0,
            };
            var allocator = Phase23Fixture.MakeAllocator(
                fvk,
                flags: VmaAllocatorCreateFlags.ExtMemoryPriorityBit
                     | VmaAllocatorCreateFlags.BufferDeviceAddressBit,
                handleTypes: handleTypes);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 0b01 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags    = VmaAllocationCreateFlags.DedicatedMemoryBit,
                Priority = 0.5f,
            };

            allocator.AllocateMemory(in req, in ci, out var alloc);

            // Priority sits at the head of the chain; export info must still be
            // reachable somewhere in it.
            Assert.Equal(StructureType.MemoryPriorityAllocateInfoExt,
                fvk.LastAllocatePNextSType);
            Assert.Equal(ExternalMemoryHandleTypeFlags.OpaqueFDBit,
                fvk.LastAllocateExportHandleTypes);

            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }
    }
}
