# exo-denoise.dll / libexo-denoise.so: Exo Denoise behind DeepFilterNet's C interface, the model embedded.
#   cmake -S capi -B build/capi -DONNXRUNTIME_DIR=<onnxruntime package root> -DEXO_DENOISE_MODEL=out/exo-denoise.onnx
#   cmake --build build/capi --config Release
# ONNXRUNTIME_DIR holds include/ (or build/native/include/) and lib/ (or runtimes/<rid>/native/), as the release
# archives from github.com/microsoft/onnxruntime and the Microsoft.ML.OnnxRuntime NuGet package lay them out.
cmake_minimum_required(VERSION 3.20)
project(exo_denoise CXX)
set(CMAKE_CXX_STANDARD 17)
set(ONNXRUNTIME_DIR "" CACHE PATH "ONNX Runtime package root")
set(EXO_DENOISE_MODEL "" CACHE FILEPATH "exo-denoise.onnx to embed")

find_path(ORT_INCLUDE onnxruntime_cxx_api.h PATHS ${ONNXRUNTIME_DIR}/include ${ONNXRUNTIME_DIR}/build/native/include REQUIRED NO_DEFAULT_PATH)
find_library(ORT_LIB onnxruntime PATHS ${ONNXRUNTIME_DIR}/lib ${ONNXRUNTIME_DIR}/runtimes/win-x64/native ${ONNXRUNTIME_DIR}/runtimes/linux-x64/native REQUIRED NO_DEFAULT_PATH)

# The model as a byte array (embed.cmake), rebuilt when the model changes.
set(MODEL_HEADER ${CMAKE_CURRENT_BINARY_DIR}/model_data.h)
add_custom_command(OUTPUT ${MODEL_HEADER}
  COMMAND ${CMAKE_COMMAND} -DIN=${EXO_DENOISE_MODEL} -DOUT=${MODEL_HEADER} -P ${CMAKE_CURRENT_SOURCE_DIR}/embed.cmake
  DEPENDS ${EXO_DENOISE_MODEL} ${CMAKE_CURRENT_SOURCE_DIR}/embed.cmake)

add_library(exo-denoise SHARED exo_denoise.cc ${MODEL_HEADER})
target_include_directories(exo-denoise PRIVATE ${ORT_INCLUDE} ${CMAKE_CURRENT_BINARY_DIR})
target_link_libraries(exo-denoise PRIVATE ${ORT_LIB})
set_target_properties(exo-denoise PROPERTIES CXX_VISIBILITY_PRESET hidden)
if(MSVC)
  target_compile_options(exo-denoise PRIVATE /O2 /EHsc)
  set_property(TARGET exo-denoise PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
endif()
