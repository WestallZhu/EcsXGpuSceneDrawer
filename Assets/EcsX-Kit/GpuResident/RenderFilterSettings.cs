using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Entities.Graphics
{

    public struct RenderFilterSettings : ISharedComponentData, IEquatable<RenderFilterSettings>
    {

        public int Layer;

        public uint RenderingLayerMask;

        public MotionVectorGenerationMode MotionMode;

        public ShadowCastingMode ShadowCastingMode;

        public bool ReceiveShadows;

        public bool StaticShadowCaster;

        public int sortingOrder;

        public static RenderFilterSettings Default => new RenderFilterSettings
        {
            Layer = 0,
            RenderingLayerMask = 0xffffffff,
            MotionMode = MotionVectorGenerationMode.Object,
            ShadowCastingMode = ShadowCastingMode.On,
            ReceiveShadows = true,
            StaticShadowCaster = false,
            sortingOrder = 0,
        };

        public bool IsInMotionPass =>
            MotionMode != MotionVectorGenerationMode.Camera;

        public override bool Equals(object obj)
        {
            if (obj is RenderFilterSettings)
                return Equals((RenderFilterSettings) obj);

            return false;
        }

        public bool Equals(RenderFilterSettings other)
        {
            return Layer == other.Layer && RenderingLayerMask == other.RenderingLayerMask && MotionMode == other.MotionMode && ShadowCastingMode == other.ShadowCastingMode && ReceiveShadows == other.ReceiveShadows && StaticShadowCaster == other.StaticShadowCaster && sortingOrder == other.sortingOrder;
        }

        public override int GetHashCode()
        {
            var hash = new xxHash3.StreamingState(true);
            hash.Update(Layer);
            hash.Update(RenderingLayerMask);
            hash.Update(MotionMode);
            hash.Update(ShadowCastingMode);
            hash.Update(ReceiveShadows);
            hash.Update(StaticShadowCaster);
            hash.Update(sortingOrder);
            return (int)hash.DigestHash64().x;
        }

        public static bool operator ==(RenderFilterSettings left, RenderFilterSettings right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RenderFilterSettings left, RenderFilterSettings right)
        {
            return !left.Equals(right);
        }
    }
}
