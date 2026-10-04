#pragma once

#if defined(ORION_NATIVE_BUILD)
#define ORION_NATIVE_API __declspec(dllexport)
#else
#define ORION_NATIVE_API __declspec(dllimport)
#endif

// Call explicitly from a Windows/Wine host. Never perform I/O under the loader lock.
extern "C" ORION_NATIVE_API void OrionNativeHello();
