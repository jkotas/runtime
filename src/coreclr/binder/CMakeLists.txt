set(CMAKE_INCLUDE_CURRENT_DIR ON)
include_directories(BEFORE "../vm/${ARCH_SOURCES_DIR}")
include_directories(BEFORE "../vm")
include_directories(BEFORE "inc")

set(BINDER_COMMON_SOURCES
    textualidentityparser.cpp
)

set(BINDER_COMMON_HEADERS
    inc/assemblyidentity.hpp
    inc/assemblyversion.hpp
    inc/assemblyversion.inl
    inc/bindertypes.hpp
    inc/textualidentityparser.hpp
)

set(BINDER_SOURCES
    ${BINDER_COMMON_SOURCES}
)

set(BINDER_HEADERS
    ${BINDER_COMMON_HEADERS}
)

if (CLR_CMAKE_TARGET_WIN32)
    list(APPEND BINDER_SOURCES ${BINDER_HEADERS})
endif(CLR_CMAKE_TARGET_WIN32)

convert_to_absolute_path(BINDER_SOURCES ${BINDER_SOURCES})

add_library_clr(v3binder_obj
    OBJECT
    ${BINDER_SOURCES}
)
add_library(v3binder INTERFACE)
target_sources(v3binder INTERFACE $<TARGET_OBJECTS:v3binder_obj>)
