using UnityEditor;
using UnityEngine;
#if USE_LUA
using XLua;
#endif

namespace Unity.Entities
{

    [ExecuteAlways]
    [AddComponentMenu("Hidden/Disabled")]
    public class PlayerLoopDisableManager : MonoBehaviour
    {
        public bool IsActive;

        #if USE_LUA
        LuaFunction unloadLuaFunction;
        #endif
        public void OnEnable()
        {
            Debug.LogWarning($"FBoard Test, OnEnable, PlayerLoopDisableManagerType: {GetType().AssemblyQualifiedName}");
            if (!IsActive)
                return;

            IsActive = false;
            DestroyImmediate(gameObject);
        }

        #if USE_LUA

        public void SetUnloadFunction(LuaFunction unload)
        {
            Debug.LogWarning($"FBoard Test, SetUnloadFunction, IsActive: {IsActive}, PlayerLoopDisableManagerType: {GetType().AssemblyQualifiedName}");
            unloadLuaFunction?.Dispose();
            unloadLuaFunction = unload;
        }
        public void OnDisable()
        {
            if (IsActive && unloadLuaFunction != null)
                unloadLuaFunction.Call();
        }

        public void OnDestroy()
        {
            unloadLuaFunction?.Dispose();
            unloadLuaFunction = null;
        }
        #endif
    }
}
