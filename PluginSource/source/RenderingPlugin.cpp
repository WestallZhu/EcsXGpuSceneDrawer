//low level rendering Unity plugin

#include "PlatformBase.h"
#include "RenderAPI.h"

#include <assert.h>
#include <math.h>
#include <vector>



struct PendingTextureUpdate
{
	void* textureHandle;
	int xoffset;
	int yoffset;
	int width;
	int height;
	int pixelsByte;
	void* dataPtr;
};
static std::vector<PendingTextureUpdate> g_PendingTextureUpdates;


// --------------------------------------------------------------------------
// UnitySetInterfaces

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType);

static IUnityInterfaces* s_UnityInterfaces = NULL;
static IUnityGraphics* s_Graphics = NULL;

extern "C" void	UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
	s_UnityInterfaces = unityInterfaces;
	s_Graphics = s_UnityInterfaces->Get<IUnityGraphics>();
	s_Graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
	
#if SUPPORT_VULKAN
	if (s_Graphics->GetRenderer() == kUnityGfxRendererNull)
	{
		extern void RenderAPI_Vulkan_OnPluginLoad(IUnityInterfaces*);
		RenderAPI_Vulkan_OnPluginLoad(unityInterfaces);
	}
#endif // SUPPORT_VULKAN

	// Run OnGraphicsDeviceEvent(initialize) manually on plugin load
	OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
	s_Graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
}

#if UNITY_WEBGL
typedef void	(UNITY_INTERFACE_API * PluginLoadFunc)(IUnityInterfaces* unityInterfaces);
typedef void	(UNITY_INTERFACE_API * PluginUnloadFunc)();

extern "C" void	UnityRegisterRenderingPlugin(PluginLoadFunc loadPlugin, PluginUnloadFunc unloadPlugin);

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API RegisterPlugin()
{
	UnityRegisterRenderingPlugin(UnityPluginLoad, UnityPluginUnload);
}
#endif

// --------------------------------------------------------------------------
// GraphicsDeviceEvent


static RenderAPI* s_CurrentAPI = NULL;
static UnityGfxRenderer s_DeviceType = kUnityGfxRendererNull;


static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
{
	// Create graphics API implementation upon initialization
	if (eventType == kUnityGfxDeviceEventInitialize)
	{
		assert(s_CurrentAPI == NULL);
		s_DeviceType = s_Graphics->GetRenderer();
		s_CurrentAPI = CreateRenderAPI(s_DeviceType);
	}

	// Let the implementation process the device related events
	if (s_CurrentAPI)
	{
		s_CurrentAPI->ProcessDeviceEvent(eventType, s_UnityInterfaces);
	}

	// Cleanup graphics API implementation upon shutdown
	if (eventType == kUnityGfxDeviceEventShutdown)
	{
		delete s_CurrentAPI;
		s_CurrentAPI = NULL;
		s_DeviceType = kUnityGfxRendererNull;
	}
}



// --------------------------------------------------------------------------
// OnRenderEvent
// This will be called for GL.IssuePluginEvent script calls; eventID will
// be the integer passed to IssuePluginEvent. In this example, we just ignore
// that value.


static void UNITY_INTERFACE_API OnRenderEvent(int eventID)
{
	// Unknown / unsupported graphics device type? Do nothing
	if (s_CurrentAPI == NULL)
		return;

	if (eventID == 3)
	{
		for (std::vector<PendingTextureUpdate>::const_iterator it = g_PendingTextureUpdates.begin(); it != g_PendingTextureUpdates.end(); ++it)
		{
			const PendingTextureUpdate& update = *it;
			s_CurrentAPI->UpdateTexture2DSub(update.textureHandle, update.xoffset, update.yoffset, update.width, update.height, update.pixelsByte, update.dataPtr);
		}
		g_PendingTextureUpdates.clear();
	}

}

// --------------------------------------------------------------------------
// GetRenderEventFunc, an example function we export which is used to get a rendering event callback function.

extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API GetRenderEventFunc()
{
	return OnRenderEvent;
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UpdateTexture2DSub(void* textureHandle, int xoffset, int yoffset, int width, int height, int pixelsByte, void* dataPtr)
{
	if (s_CurrentAPI == NULL || textureHandle == NULL || dataPtr == NULL)
		return;

		PendingTextureUpdate update;
		update.textureHandle = textureHandle;
		update.xoffset = xoffset;
		update.yoffset = yoffset;
		update.width = width;
		update.height = height;
		update.pixelsByte = pixelsByte;
		update.dataPtr = dataPtr;
		g_PendingTextureUpdates.push_back(update);
}

// --------------------------------------------------------------------------
// DX12 plugin specific
// --------------------------------------------------------------------------

extern "C" UNITY_INTERFACE_EXPORT void* UNITY_INTERFACE_API GetRenderTexture()
{
	return s_CurrentAPI->getRenderTexture();
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API SetRenderTexture(UnityRenderBuffer rb)
{
	s_CurrentAPI->setRenderTextureResource(rb);
}

extern "C" UNITY_INTERFACE_EXPORT bool UNITY_INTERFACE_API IsSwapChainAvailable()
{
	return s_CurrentAPI->isSwapChainAvailable();
}

extern "C" UNITY_INTERFACE_EXPORT unsigned int UNITY_INTERFACE_API GetPresentFlags()
{
	return s_CurrentAPI->getPresentFlags();
}

extern "C" UNITY_INTERFACE_EXPORT unsigned int UNITY_INTERFACE_API GetSyncInterval()
{
	return s_CurrentAPI->getSyncInterval();
}

extern "C" UNITY_INTERFACE_EXPORT unsigned int UNITY_INTERFACE_API GetBackBufferWidth()
{
	return s_CurrentAPI->getBackbufferHeight();
}

extern "C" UNITY_INTERFACE_EXPORT unsigned int UNITY_INTERFACE_API GetBackBufferHeight()
{
	return s_CurrentAPI->getBackbufferWidth();
}
