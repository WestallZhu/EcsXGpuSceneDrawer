using Unity.Mathematics;
using Unity.Entities;
using Unity.Rendering;

[MaterialProperty("_SpriteColor")]
public struct SpriteColor : IComponentData
{

    public float value;
}

[MaterialProperty("_SpriteFlipX")]
public struct SpriteFlipX : IComponentData
{

    public float value;
}

[MaterialProperty("_SpriteFlipY")]
public struct SpriteFlipY : IComponentData
{

    public float value;
}

[MaterialProperty("_SpriteDrawMode")]
public struct SpriteEntityDrawMode : IComponentData
{

    public float value;
}

[MaterialProperty("_SpriteRectParam")]
public struct SpriteRectParam : IComponentData
{

    public float4 value;
}

[MaterialProperty("_SpriteTileSize")]
public struct SpriteTileSize : IComponentData
{

    public float value;
}

[MaterialProperty("_SpriteAnimationSpeed")]
public struct SpriteAnimationSpeed : IComponentData
{

    public float value;
}

[MaterialProperty("_SpritePivotScale")]
public struct SpritePivotScale : IComponentData
{

    public float4 value;
}

[MaterialProperty("_SpriteFadeStartTime")]
public struct SpriteFadeStartTime : IComponentData
{

    public float time;
}

[MaterialProperty("_SpriteBillBord")]
public struct SpriteBillBord : IComponentData
{

    public float2 Value;
}

[MaterialProperty("_SpriteFitDynamicScaleX")]
public struct SpriteFitDynamicScaleX : IComponentData
{

    public float value;
}