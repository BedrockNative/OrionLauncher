#include "Orion.Native.h"
#include <iostream>

extern "C" ORION_NATIVE_API void OrionNativeHello()
{
    std::cout << "Hello world from Orion.Native!" << std::endl;
}
