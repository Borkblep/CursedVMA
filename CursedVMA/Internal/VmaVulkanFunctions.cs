// Concrete IVulkanFunctions backed by a Silk.NET Vk dispatch table. All
// methods delegate directly to the underlying Vulkan API; no VMA logic lives
// here. An optional KhrBindMemory2 extension is accepted for devices that
// expose VK_KHR_bind_memory2 without promoting it to Vulkan 1.1 core.

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System.Runtime.CompilerServices;

namespace CursedVMA.Internal
{
    internal sealed unsafe class VmaVulkanFunctions : IVulkanFunctions
    {
        private readonly Vk m_Vk;
        private readonly KhrBindMemory2? m_KhrBindMemory2;

        internal VmaVulkanFunctions(Vk vk, KhrBindMemory2? khrBindMemory2 = null)
        {
            m_Vk = vk;
            m_KhrBindMemory2 = khrBindMemory2;
        }

        public Result AllocateMemory(
            Device device,
            in MemoryAllocateInfo allocateInfo,
            AllocationCallbacks* pAllocator,
            out DeviceMemory memory)
        {
            DeviceMemory m = default;
            Result r;
            fixed (MemoryAllocateInfo* pInfo = &allocateInfo)
                r = m_Vk.AllocateMemory(device, pInfo, pAllocator, &m);
            memory = m;
            return r;
        }

        public void FreeMemory(Device device, DeviceMemory memory, AllocationCallbacks* pAllocator)
            => m_Vk.FreeMemory(device, memory, pAllocator);

        public Result MapMemory(
            Device device,
            DeviceMemory memory,
            ulong offset,
            ulong size,
            MemoryMapFlags flags,
            out void* ppData)
        {
            void* p = null;
            Result r = m_Vk.MapMemory(device, memory, offset, size, flags, &p);
            ppData = p;
            return r;
        }

        public void UnmapMemory(Device device, DeviceMemory memory)
            => m_Vk.UnmapMemory(device, memory);

        public Result FlushMappedMemoryRanges(
            Device device,
            uint memoryRangeCount,
            MappedMemoryRange* pMemoryRanges)
            => m_Vk.FlushMappedMemoryRanges(device, memoryRangeCount, pMemoryRanges);

        public Result InvalidateMappedMemoryRanges(
            Device device,
            uint memoryRangeCount,
            MappedMemoryRange* pMemoryRanges)
            => m_Vk.InvalidateMappedMemoryRanges(device, memoryRangeCount, pMemoryRanges);

        public Result BindBufferMemory(
            Device device,
            Silk.NET.Vulkan.Buffer buffer,
            DeviceMemory memory,
            ulong memoryOffset)
            => m_Vk.BindBufferMemory(device, buffer, memory, memoryOffset);

        public Result BindBufferMemory2(
            Device device,
            uint bindInfoCount,
            BindBufferMemoryInfo* pBindInfos)
        {
            if (m_KhrBindMemory2 != null)
                return m_KhrBindMemory2.BindBufferMemory2(device, bindInfoCount, pBindInfos);
            return m_Vk.BindBufferMemory2(device, bindInfoCount, pBindInfos);
        }

        public Result BindImageMemory(
            Device device,
            Image image,
            DeviceMemory memory,
            ulong memoryOffset)
            => m_Vk.BindImageMemory(device, image, memory, memoryOffset);

        public Result BindImageMemory2(
            Device device,
            uint bindInfoCount,
            BindImageMemoryInfo* pBindInfos)
        {
            if (m_KhrBindMemory2 != null)
                return m_KhrBindMemory2.BindImageMemory2(device, bindInfoCount, pBindInfos);
            return m_Vk.BindImageMemory2(device, bindInfoCount, pBindInfos);
        }

        public void GetBufferMemoryRequirements(
            Device device,
            Silk.NET.Vulkan.Buffer buffer,
            out MemoryRequirements memoryRequirements)
        {
            MemoryRequirements r = default;
            m_Vk.GetBufferMemoryRequirements(device, buffer, &r);
            memoryRequirements = r;
        }

        public void GetImageMemoryRequirements(
            Device device,
            Image image,
            out MemoryRequirements memoryRequirements)
        {
            MemoryRequirements r = default;
            m_Vk.GetImageMemoryRequirements(device, image, &r);
            memoryRequirements = r;
        }

        public unsafe void GetBufferMemoryRequirements2(
            Device device,
            Silk.NET.Vulkan.Buffer buffer,
            ref MemoryRequirements2 memoryRequirements)
        {
            var info = new BufferMemoryRequirementsInfo2
            {
                SType  = StructureType.BufferMemoryRequirementsInfo2,
                Buffer = buffer,
            };
            m_Vk.GetBufferMemoryRequirements2(
                device, &info,
                (MemoryRequirements2*)Unsafe.AsPointer(ref memoryRequirements));
        }

        public unsafe void GetImageMemoryRequirements2(
            Device device,
            Image image,
            ref MemoryRequirements2 memoryRequirements)
        {
            var info = new ImageMemoryRequirementsInfo2
            {
                SType = StructureType.ImageMemoryRequirementsInfo2,
                Image = image,
            };
            m_Vk.GetImageMemoryRequirements2(
                device, &info,
                (MemoryRequirements2*)Unsafe.AsPointer(ref memoryRequirements));
        }

        public void GetPhysicalDeviceMemoryProperties(
            PhysicalDevice physicalDevice,
            out PhysicalDeviceMemoryProperties memoryProperties)
        {
            PhysicalDeviceMemoryProperties p = default;
            m_Vk.GetPhysicalDeviceMemoryProperties(physicalDevice, &p);
            memoryProperties = p;
        }

        public void GetPhysicalDeviceProperties(
            PhysicalDevice physicalDevice,
            out PhysicalDeviceProperties properties)
        {
            PhysicalDeviceProperties p = default;
            m_Vk.GetPhysicalDeviceProperties(physicalDevice, &p);
            properties = p;
        }

        public unsafe void GetPhysicalDeviceMemoryProperties2(
            PhysicalDevice physicalDevice,
            ref PhysicalDeviceMemoryProperties2 memoryProperties2)
            => m_Vk.GetPhysicalDeviceMemoryProperties2(
                physicalDevice,
                (PhysicalDeviceMemoryProperties2*)Unsafe.AsPointer(ref memoryProperties2));

        public Result CreateBuffer(
            Device device,
            in BufferCreateInfo createInfo,
            AllocationCallbacks* pAllocator,
            out Silk.NET.Vulkan.Buffer buffer)
        {
            Silk.NET.Vulkan.Buffer b = default;
            Result r;
            fixed (BufferCreateInfo* pInfo = &createInfo)
                r = m_Vk.CreateBuffer(device, pInfo, pAllocator, &b);
            buffer = b;
            return r;
        }

        public void DestroyBuffer(
            Device device,
            Silk.NET.Vulkan.Buffer buffer,
            AllocationCallbacks* pAllocator)
            => m_Vk.DestroyBuffer(device, buffer, pAllocator);

        public Result CreateImage(
            Device device,
            in ImageCreateInfo createInfo,
            AllocationCallbacks* pAllocator,
            out Image image)
        {
            Image i = default;
            Result r;
            fixed (ImageCreateInfo* pInfo = &createInfo)
                r = m_Vk.CreateImage(device, pInfo, pAllocator, &i);
            image = i;
            return r;
        }

        public void DestroyImage(
            Device device,
            Image image,
            AllocationCallbacks* pAllocator)
            => m_Vk.DestroyImage(device, image, pAllocator);
    }
}
