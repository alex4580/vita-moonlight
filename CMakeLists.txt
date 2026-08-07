cmake_minimum_required(VERSION 3.2)

# Vita packages are user-facing runtime artifacts. Single-configuration
# generators otherwise default to an empty build type, which produces an
# effectively unoptimized GCC build. Keep local/fork builds safe by default;
# callers can still request Debug explicitly.
if(NOT CMAKE_CONFIGURATION_TYPES AND NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
  if(DEFINED ENV{VITASDK})
	set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
  else()
	message(FATAL_ERROR "Please define VITASDK to point to your SDK path!")
  endif()
endif()
project(moonlight)

include("$ENV{VITASDK}/share/vita.cmake" REQUIRED)
# NOTE: release.py updates these values together with the Windows host,
# installer, manual, and LiveArea metadata.
set(TITLE_ID "XYZZ00002")
set(TITLE "Moonlight")
set(VERSION_MAJOR "0")
set(VERSION_MINOR "14")
set(VERSION_PATCH "8")

execute_process(COMMAND
	python -c "print('%02d.%02d' % (${VERSION_MAJOR}, ${VERSION_MINOR}))"
	OUTPUT_VARIABLE APP_VER
)
string(REPLACE "\n" "" APP_VER "${APP_VER}")

add_definitions(-DVERSION_MAJOR=${VERSION_MAJOR})
add_definitions(-DVERSION_MINOR=${VERSION_MINOR})
add_definitions(-DVERSION_PATCH=${VERSION_PATCH})
# Bundled dependencies also select Vita-specific socket, UUID, and threading
# implementations with this symbol, so the definition must remain global.
add_definitions(-D__vita__)

include_directories(
	include/
	src/
	third_party/moonlight-common-c/src/
	third_party/moonlight-common-c/reedsolomon/
	libgamestream/
	third_party/libuuid/src/
	third_party/h264bitstream/
	third_party/enet/include/
	third_party/inih/
	third_party/mdnsniff/
)


add_executable(${PROJECT_NAME}.elf
	src/config.c
	src/input/mapping.c
	src/connection.c
	src/debug.c
	src/main.c
	src/platform.c
	src/util.c
	src/device.c
	src/check_dir.c
	src/wake_on_lan.c
	src/audio/vita.c
	src/video/vita.c
	src/input/vita.c
	src/input/shortcuts.c
	src/power/vita.c
	src/input/motion.c
	src/graphics.c
	src/font.c
	src/gui/guilib.c
	src/gui/ime.c
	src/gui/ui.c
	src/gui/ui_settings.c
	src/gui/ui_controller_mapper.c
	src/gui/ui_connect.c
	src/gui/ui_device.c
	src/gui/ui_check.c
	src/gui/ui_stream_overlay.c
	src/gui/ui_diagnostics.c

	libgamestream/client.c
	libgamestream/http.c
	libgamestream/mkcert.c
	libgamestream/sps.c
	libgamestream/xml.c

	src/keyboardsystem.c
	src/input/keyboardkeys.c
	src/gui/ui_keyboard.c
	src/input/touchabsolute.c
	src/check_host.c
)

target_compile_definitions(${PROJECT_NAME}.elf PRIVATE _POSIX_C_SOURCE=200809L)
set_property(TARGET ${PROJECT_NAME}.elf PROPERTY C_STANDARD 99)
set_property(TARGET ${PROJECT_NAME}.elf PROPERTY C_STANDARD_REQUIRED ON)
set_property(TARGET ${PROJECT_NAME}.elf PROPERTY C_EXTENSIONS OFF)
target_compile_options(${PROJECT_NAME}.elf PRIVATE
	-Wall
	-Wextra
	-Wno-unused-parameter
	-Wno-pointer-sign
	-Wno-sign-compare
	-Wno-switch
	$<$<CONFIG:Debug>:-g3>
	$<$<CONFIG:Release>:-O2>
)


# Por defecto, solo enlazamos curl y el resto de libs estándar:
target_link_libraries(${PROJECT_NAME}.elf
	-Wl,-q
	-lexpat
	-lcurl
	-lzstd
	moonlight-common-c
	enet
	h264bitstream
	inih
	libuuid
	mdnsniff
	-lz
	-lssl
	-lcrypto
	-lopus
	-lbz2
	-lpthread
	-lSceDisplay_stub
	-lSceCtrl_stub
	-lSceTouch_stub
	-lSceNet_stub
	-lSceNetCtl_stub
	-lScePower_stub
	-lSceVideodec_stub
	-lSceCommonDialog_stub
	-lSceShellSvc_stub
	-lSceAppMgr_stub
	-lSceAudio_stub
	-lSceIme_stub
	-lSceMotion_stub
	-lvita2d
	-lSceGxm_stub
	-lScePgf_stub
	-lSceCommonDialog_stub
	-lfreetype
	-lpng
	-ljpeg
	-lz
	-lm
	-lc
)

add_custom_target(${PROJECT_NAME}.velf ALL
	COMMAND vita-elf-create ${PROJECT_NAME}.elf ${PROJECT_NAME}.velf
)

add_custom_target(param.sfo ALL   
	COMMAND vita-mksfoex -s TITLE_ID=${TITLE_ID} -s APP_VER=${APP_VER} "${TITLE}" param.sfo
)

# Actualizar versión en template.xml automáticamente antes de empaquetar el VPK (solo la línea visual)
add_custom_target(update_template_version ALL
	COMMAND sed -i '/<str size=\"18.*>v[0-9]*\.[0-9]*\.[0-9]*<\\/str>/s/v[0-9]*\.[0-9]*\.[0-9]*/v${VERSION_MAJOR}.${VERSION_MINOR}.${VERSION_PATCH}/' sce_sys/livearea/contents/template.xml
	WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
)

add_custom_target(${PROJECT_NAME}.vpk ALL
	COMMAND vita-make-fself -a 0x2800000000000001 -at 0x0E -c ${PROJECT_NAME}.velf eboot.bin
	COMMAND vita-pack-vpk -s param.sfo -b eboot.bin
			--add ../sce_sys/icon0.png=sce_sys/icon0.png
			--add ../sce_sys/livearea/contents/bg.png=sce_sys/livearea/contents/bg.png
			--add ../sce_sys/livearea/contents/startup.png=sce_sys/livearea/contents/startup.png
			--add ../sce_sys/livearea/contents/template.xml=sce_sys/livearea/contents/template.xml
			--add ../assets/nerdfont.ttf=assets/nerdfont.ttf
			${PROJECT_NAME}.vpk
)

add_dependencies(${PROJECT_NAME}.velf ${PROJECT_NAME}.elf)
add_dependencies(${PROJECT_NAME}.vpk ${PROJECT_NAME}.velf param.sfo update_template_version)

add_subdirectory(third_party)

option(USE_DIR_UMA0 "Forzar uso de uma0:data/moonlight como directorio de claves/config" OFF)
if(USE_DIR_UMA0)
  add_definitions(-DUSE_DIR_UMA0)
endif()
