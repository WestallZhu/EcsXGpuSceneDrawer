using System;

namespace Unity.Rendering
{

    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
    public class MaterialPropertyAttribute : Attribute
    {

        public MaterialPropertyAttribute(string materialPropertyName, short overrideSizeGPU = -1)
        {
            Name = materialPropertyName;
            OverrideSizeGPU = overrideSizeGPU;
        }

        public string Name { get; }

        public short OverrideSizeGPU { get; }
    }
}
