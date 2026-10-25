# Writes IN as a C byte array to OUT.
file(READ ${IN} hex HEX)
string(LENGTH "${hex}" n)
math(EXPR size "${n} / 2")
string(REGEX REPLACE "([0-9a-f][0-9a-f])" "0x\\1," bytes "${hex}")
file(WRITE ${OUT} "#pragma once\n#include <cstddef>\nalignas(16) static const unsigned char exo_denoise_model[] = {${bytes}};\nstatic const std::size_t exo_denoise_model_size = ${size};\n")
