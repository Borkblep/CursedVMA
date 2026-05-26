// Phase 24c tests: assorted behavioral-contract gaps from the review plan.
//
// Each test fixture below covers a small contract slice. The fixtures share
// a common 1-type, 1-heap fake to keep memory-type selection deterministic.

using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    internal static class ContractFixture
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
                                  | MemoryPropertyFlags.HostCoherentBit
                                  | MemoryPropertyFlags.DeviceLocalBit,
                };
            }
            return fvk;
        }

        internal static VmaAllocator MakeAllocator(FakeVulkanFunctions fvk,
            VmaAllocatorCreateFlags flags = VmaAllocatorCreateFlags.None)
        {
            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
                Flags          = flags,
            };
            VmaAllocator.Create(fvk, ci, out var a);
            return a!;
        }
    }

    // ── DedicatedMemoryBit 1:1 with vkAllocateMemory/vkFreeMemory ──────────

    public sealed class VmaDedicatedAllocationCountTests
    {
        [Fact]
        public void TwoDedicatedAllocations_ProduceTwoVkAllocateMemoryCalls()
        {
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };

            allocator.AllocateMemory(in req, in ci, out var a1);
            allocator.AllocateMemory(in req, in ci, out var a2);

            Assert.Equal(2, fvk.AllocateMemoryCallCount);

            allocator.FreeMemory(a1);
            allocator.FreeMemory(a2);
            Assert.Equal(2, fvk.FreeMemoryCallCount);

            allocator.Dispose();
        }

        [Fact]
        public void DedicatedAllocation_FreeMemory_ProducesExactlyOneVkFreeMemoryCall()
        {
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };

            allocator.AllocateMemory(in req, in ci, out var a);
            int before = fvk.FreeMemoryCallCount;
            allocator.FreeMemory(a);

            Assert.Equal(before + 1, fvk.FreeMemoryCallCount);
            allocator.Dispose();
        }
    }

    // ── MappedBit lifecycle ─────────────────────────────────────────────────

    public sealed class VmaMappedBitLifecycleTests
    {
        [Fact]
        public unsafe void MappedBit_MappedDataIsImmediatelyNonNull()
        {
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.MappedBit };

            allocator.AllocateMemory(in req, in ci, out var alloc);
            allocator.GetAllocationInfo(alloc!, out var info);

            // No explicit MapMemory call required.
            Assert.True(info.MappedData != null);

            allocator.UnmapMemory(alloc!);
            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }

        [Fact]
        public void MappedBit_AllocationLifecycle_BalancesMapAndUnmap()
        {
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.MappedBit };

            int mapsBefore   = fvk.MapMemoryCallCount;
            int unmapsBefore = fvk.UnmapMemoryCallCount;

            allocator.AllocateMemory(in req, in ci, out var alloc);
            Assert.Equal(mapsBefore + 1, fvk.MapMemoryCallCount);

            // Drop the persistent mapping then free. Block destruction triggers
            // an additional unmap for the underlying VkDeviceMemory.
            allocator.UnmapMemory(alloc!);
            allocator.FreeMemory(alloc);

            Assert.True(fvk.UnmapMemoryCallCount > unmapsBefore);
            allocator.Dispose();
        }
    }

    // ── GetAllocationInfo2.BlockSize for pool allocations ──────────────────

    public sealed class VmaGetAllocationInfo2PoolTests
    {
        [Fact]
        public void PoolAllocation_BlockSizeEqualsPoolBlockSize()
        {
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            const ulong poolBlockSize = 4ul * 1024 * 1024;
            var poolCi = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                BlockSize       = poolBlockSize,
            };
            allocator.CreatePool(in poolCi, out var pool);

            var req = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var aci = new VmaAllocationCreateInfo { Pool = pool };
            allocator.AllocateMemory(in req, in aci, out var alloc);

            allocator.GetAllocationInfo2(alloc!, out var info2);
            Assert.Equal(poolBlockSize, info2.BlockSize);
            Assert.False(info2.DedicatedMemory);
            Assert.Equal(64ul, info2.AllocationInfo.Size);

            allocator.FreeMemory(alloc);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }

        [Fact]
        public void DedicatedAllocation_BlockSizeEqualsAllocationSize()
        {
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            var req = new MemoryRequirements
            { Size = 1234, Alignment = 1, MemoryTypeBits = 1 };
            var aci = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.DedicatedMemoryBit };
            allocator.AllocateMemory(in req, in aci, out var alloc);

            allocator.GetAllocationInfo2(alloc!, out var info2);
            Assert.Equal(1234ul, info2.BlockSize);
            Assert.True(info2.DedicatedMemory);

            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }
    }

    // ── NeverAllocateBit ────────────────────────────────────────────────────

    public sealed class VmaNeverAllocateBitTests
    {
        [Fact]
        public void NeverAllocateBit_EmptyDefaultAllocator_ReturnsOutOfDeviceMemory()
        {
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            var req = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.NeverAllocateBit };

            int allocsBefore = fvk.AllocateMemoryCallCount;
            Result r = allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);
            Assert.Null(alloc);
            Assert.Equal(allocsBefore, fvk.AllocateMemoryCallCount);

            allocator.Dispose();
        }

        [Fact]
        public void NeverAllocateBit_PoolFull_ReturnsErrorWithoutNewBlock()
        {
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            const ulong poolBlockSize = 4096;
            var poolCi = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                BlockSize       = poolBlockSize,
                MinBlockCount   = 1,
                MaxBlockCount   = 1,
            };
            allocator.CreatePool(in poolCi, out var pool);

            // Fill the pool's single block.
            var req = new MemoryRequirements
            { Size = poolBlockSize, Alignment = 1, MemoryTypeBits = 1 };
            var aci = new VmaAllocationCreateInfo { Pool = pool };
            Result r1 = allocator.AllocateMemory(in req, in aci, out var alloc);
            Assert.Equal(Result.Success, r1);

            // Next allocation with NeverAllocateBit must fail without growing.
            var smallReq = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var neverCi  = new VmaAllocationCreateInfo
            {
                Pool  = pool,
                Flags = VmaAllocationCreateFlags.NeverAllocateBit,
            };
            int allocsBefore = fvk.AllocateMemoryCallCount;
            Result r2 = allocator.AllocateMemory(in smallReq, in neverCi, out var alloc2);

            Assert.Equal(Result.ErrorOutOfDeviceMemory, r2);
            Assert.Null(alloc2);
            Assert.Equal(allocsBefore, fvk.AllocateMemoryCallCount);

            allocator.FreeMemory(alloc);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }

        [Fact]
        public void NeverAllocateBit_SpaceAvailable_Succeeds()
        {
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            // Seed a block first.
            var seedReq = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var seedCi  = new VmaAllocationCreateInfo();
            allocator.AllocateMemory(in seedReq, in seedCi, out var seed);

            // Now an allocation that should fit alongside it.
            var ci = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.NeverAllocateBit };
            Result r = allocator.AllocateMemory(in seedReq, in ci, out var alloc);

            Assert.Equal(Result.Success, r);

            allocator.FreeMemory(seed);
            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }
    }

    // ── UpperAddressBit at the allocator level ─────────────────────────────

    public sealed class VmaUpperAddressBitAllocatorTests
    {
        [Fact]
        public void UpperAddressBit_OnDefaultTlsfAllocator_ReturnsOutOfDeviceMemory()
        {
            // The default block vector is TLSF, which rejects upper-address
            // requests; the allocator should propagate the rejection.
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            var req = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.UpperAddressBit };

            Result r = allocator.AllocateMemory(in req, in ci, out var alloc);

            Assert.NotEqual(Result.Success, r);
            Assert.Null(alloc);
            allocator.Dispose();
        }

        [Fact]
        public void UpperAddressBit_OnLinearAlgorithmPool_Succeeds()
        {
            var fvk = ContractFixture.MakeFvk();
            var allocator = ContractFixture.MakeAllocator(fvk);

            var poolCi = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 0,
                Flags           = VmaPoolCreateFlags.LinearAlgorithmBit,
                BlockSize       = 4096,
                MinBlockCount   = 1,
                MaxBlockCount   = 1,
            };
            allocator.CreatePool(in poolCi, out var pool);

            var req = new MemoryRequirements
            { Size = 64, Alignment = 1, MemoryTypeBits = 1 };
            var aci = new VmaAllocationCreateInfo
            {
                Pool  = pool,
                Flags = VmaAllocationCreateFlags.UpperAddressBit,
            };
            Result r = allocator.AllocateMemory(in req, in aci, out var alloc);

            Assert.Equal(Result.Success, r);
            Assert.NotNull(alloc);

            allocator.FreeMemory(alloc);
            allocator.DestroyPool(pool!);
            allocator.Dispose();
        }
    }

    // ── WithinBudgetBit boundary ────────────────────────────────────────────

    public sealed class VmaWithinBudgetBoundaryTests
    {
        [Fact]
        public void ExactlyAtBudget_Succeeds()
        {
            // usage + size == budget is allowed; the rejection compares with
            // strict greater-than.
            var fvk = ContractFixture.MakeFvk();
            unsafe
            {
                var fb = new PhysicalDeviceMemoryBudgetPropertiesEXT();
                fb.HeapUsage[0]  = 4096;
                fb.HeapBudget[0] = 4096 + 1024; // room for exactly one 1024-byte alloc
                fvk.FakeBudgetProperties = fb;
            }

            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
                Flags          = VmaAllocatorCreateFlags.ExtMemoryBudgetBit,
            };
            VmaAllocator.Create(fvk, ci, out var allocator);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var aci = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.WithinBudgetBit };

            Result r = allocator!.AllocateMemory(in req, in aci, out var alloc);
            Assert.Equal(Result.Success, r);

            allocator.FreeMemory(alloc);
            allocator.Dispose();
        }

        [Fact]
        public void OneByteOverBudget_ReturnsOutOfDeviceMemory()
        {
            var fvk = ContractFixture.MakeFvk();
            unsafe
            {
                var fb = new PhysicalDeviceMemoryBudgetPropertiesEXT();
                fb.HeapUsage[0]  = 4096;
                fb.HeapBudget[0] = 4096 + 1023; // 1 byte short of fitting 1024
                fvk.FakeBudgetProperties = fb;
            }

            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = new Device(1),
                Flags          = VmaAllocatorCreateFlags.ExtMemoryBudgetBit,
            };
            VmaAllocator.Create(fvk, ci, out var allocator);

            var req = new MemoryRequirements
            { Size = 1024, Alignment = 1, MemoryTypeBits = 1 };
            var aci = new VmaAllocationCreateInfo
            { Flags = VmaAllocationCreateFlags.WithinBudgetBit };

            Result r = allocator!.AllocateMemory(in req, in aci, out _);
            Assert.Equal(Result.ErrorOutOfDeviceMemory, r);

            allocator.Dispose();
        }
    }

    // ── VmaAllocator.Create negative parameters ────────────────────────────

    public sealed class VmaAllocatorCreateNegativeTests
    {
        [Fact]
        public void Create_NullPhysicalDevice_ReturnsInitializationFailed()
        {
            var fvk = ContractFixture.MakeFvk();
            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = default,
                Device         = new Device(1),
            };
            Result r = VmaAllocator.Create(fvk, ci, out var a);
            Assert.Equal(Result.ErrorInitializationFailed, r);
            Assert.Null(a);
        }

        [Fact]
        public void Create_NullDevice_ReturnsInitializationFailed()
        {
            var fvk = ContractFixture.MakeFvk();
            var ci = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = new PhysicalDevice(1),
                Device         = default,
            };
            Result r = VmaAllocator.Create(fvk, ci, out var a);
            Assert.Equal(Result.ErrorInitializationFailed, r);
            Assert.Null(a);
        }
    }
}
