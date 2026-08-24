cmake_minimum_required(VERSION 3.10)
project(porta_pty LANGUAGES C)

if(NOT CMAKE_SYSTEM_NAME STREQUAL "Linux")
    message(FATAL_ERROR "LinuxPty.NET's native shim supports Linux only.")
endif()

find_package(Threads REQUIRED)

add_library(porta_pty SHARED porta_pty.c)

set_target_properties(porta_pty PROPERTIES
    C_STANDARD 99
    C_STANDARD_REQUIRED YES
    C_EXTENSIONS YES
    C_VISIBILITY_PRESET hidden
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    PREFIX "lib"
    SUFFIX ".so"
)

target_compile_options(porta_pty PRIVATE
    -Wall
    -Wextra
    $<$<CONFIG:Release>:-O2>
)

target_link_libraries(porta_pty PRIVATE util Threads::Threads)
