cmake_minimum_required(VERSION 3.24)
project(IntonacoVideo LANGUAGES CXX)

add_library(IntonacoVideo SHARED engine.cpp)
target_compile_features(IntonacoVideo PRIVATE cxx_std_20)
target_compile_definitions(IntonacoVideo PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(IntonacoVideo PRIVATE /W4 /EHsc /O2)
target_link_libraries(IntonacoVideo PRIVATE
  d3d11 dxgi mfplat mf mfuuid ole32 oleaut32
)
set_target_properties(IntonacoVideo PROPERTIES
  OUTPUT_NAME "Intonaco.Video"
  PREFIX ""
)
