cmake_minimum_required(VERSION 3.20)
project(Orion.Native VERSION 0.1.0 LANGUAGES CXX)

if(NOT WIN32)
  message(FATAL_ERROR "Orion.Native targets Windows/Wine. Use cmake/mingw-w64.cmake when building on Linux.")
endif()

add_library(Orion.Native SHARED src/hello.cpp)
target_compile_features(Orion.Native PRIVATE cxx_std_20)
set_target_properties(Orion.Native PROPERTIES
  CXX_EXTENSIONS OFF
  PREFIX ""
  OUTPUT_NAME "Orion.Native"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out")
target_include_directories(Orion.Native PUBLIC include)
target_compile_definitions(Orion.Native PRIVATE ORION_NATIVE_BUILD)
if(MINGW)
  target_compile_options(Orion.Native PRIVATE -Wall -Wextra -Wpedantic)
  target_link_options(Orion.Native PRIVATE -static -static-libgcc -static-libstdc++)
endif()
