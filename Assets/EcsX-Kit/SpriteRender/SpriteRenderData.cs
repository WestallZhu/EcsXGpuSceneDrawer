using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.Entities
{

    internal class SpriteRenderData
    {
        public static readonly Mesh SpriteQuad = CreateQuadMesh();

        public static readonly AABB fixedSpriteAABB = new AABB
        {
            Center = new float3(0, 0, 0),
            Extents = new float3(5000, 5000, 5000)
        };

        private static NativeHashMap<int, MaterialData> materialCache = new NativeHashMap<int, MaterialData>(64,Allocator.Persistent);

        struct MaterialData
        {
            public int MaterialInstanceID;
            public int ReferenceCount;

            public MaterialData(Material material)
            {
                MaterialInstanceID = material.GetInstanceID();
                ReferenceCount = 1;
            }
        }

        private static Mesh CreateQuadMesh()
        {
            float2 size = new float2(1f, 1f);
            float2 pivot = new float2(0.0f, 0.0f);
            float2 scaledPivot = size * pivot;
            Vector3[] vertices =
            {
                new Vector3(size.x - scaledPivot.x, size.y - scaledPivot.y, 0),
                new Vector3(size.x - scaledPivot.x, -scaledPivot.y, 0),
                new Vector3(-scaledPivot.x, -scaledPivot.y, 0),
                new Vector3(-scaledPivot.x, size.y - scaledPivot.y, 0),
            };

            Vector2[] uv =
            {
                new Vector2(1,1),
                new Vector2(1, 0),
                new Vector2(0, 0),
                new Vector2(0,1)
            };

            int[] triangles =
            {
                0, 1, 2,
                2, 3, 0
            };
            return new Mesh
            {
                name = "SpriteQuad",
                vertices = vertices,
                uv = uv,
                triangles = triangles
            };
        }

        public static Material GetOrCreateMaterial(Texture2D texture, bool ZTestAlways)
        {
            if (texture == null)
            {
                Debug.LogError("Texture cannot be null");
                return null;
            }
            int instanceID = (texture.GetInstanceID() & 0x3FFFFFFF) | (ZTestAlways ? unchecked((int)0x80000000) : 0x40000000);
            if (materialCache.TryGetValue(instanceID, out MaterialData materialData))
            {

                materialData.ReferenceCount++;
                materialCache[instanceID] = materialData;
                return Resources.InstanceIDToObject(materialData.MaterialInstanceID) as Material;
            }

            Material newMaterial = CreateSpriteMaterial(texture, ZTestAlways);
            materialCache.Add(instanceID, new MaterialData(newMaterial));
            return newMaterial;
        }

        public static int GetMaterialInstanceID(Texture2D texture, bool ZTestAlways)
        {
            if(texture == null)
                return 0;
            int instanceID = (texture.GetInstanceID() & 0x3FFFFFFF) | (ZTestAlways ? unchecked((int)0x80000000) : 0x40000000);
            if (materialCache.TryGetValue(instanceID, out MaterialData materialData))
            {
                return materialData.MaterialInstanceID;
            }
            return 0;
        }
        public static void ReleaseMaterial(Material material)
        {
            if (material == null)
                return;
            if (material.shader == null)
                return;
            if (!material.shader.name.StartsWith("Custom/SpriteEcs"))
                return;
            if ((material.hideFlags & HideFlags.HideAndDontSave) == 0)
                return;

            var texture = material.mainTexture as Texture2D;
            if (texture == null)
                return;
            int zTestFlag = 0;
            if (material.HasProperty("_ZTestAlwaysFlag"))
                zTestFlag = material.GetInt("_ZTestAlwaysFlag");
            bool ZTestAlways = (zTestFlag == 1);
            int instanceID = (texture.GetInstanceID() & 0x3FFFFFFF) | (ZTestAlways ? unchecked((int)0x80000000) : 0x40000000);

            if (materialCache.TryGetValue(instanceID, out MaterialData materialData))
            {
                materialData.ReferenceCount--;
                if (materialData.ReferenceCount <= 0)
                {
                    Object.Destroy(material);
                    materialCache.Remove(instanceID);
                }
                else
                {
                    materialCache[instanceID] = materialData;
                }
            }
        }

        public static int GetMaterialReferenceCount(Texture2D texture)
        {
            if (texture != null && materialCache.TryGetValue(texture.GetInstanceID(), out MaterialData materialData))
            {
                return materialData.ReferenceCount;
            }
            return -1;
        }

        public static Material CreateSpriteMaterial(Texture2D texture, bool ZTestAlways)
        {
            Shader spriteShader = null;
            if (!ZTestAlways)
                spriteShader = Shader.Find("Custom/SpriteEcs");
            else
                spriteShader = Shader.Find("Custom/SpriteEcsZTestAlway");
            if (spriteShader == null)
            {
                Debug.LogError($"{spriteShader} shader not found");
                return null;
            }
            Material material = new Material(spriteShader)
            {
                name = $"SpriteMaterial_{texture.name}_DM{ZTestAlways}",
                mainTexture = texture,
                enableInstancing = true,
                hideFlags = HideFlags.HideAndDontSave
            };
            material.SetInt("_ZTestAlwaysFlag", ZTestAlways ? 1 : 0);
            return material;
        }

        private static void ClearMaterialCache()
        {
            foreach (var materialData in materialCache)
            {
                var mat = Resources.InstanceIDToObject(materialData.Value.MaterialInstanceID);
                if (mat != null)
                {
                    Object.Destroy(mat);
                }
            }
            materialCache.Clear();
        }

        public static void Release()
        {
            ClearMaterialCache();
        }
    }
}