add_custom_command(
  OUTPUT vita-moonlight.1
  COMMAND pod2man
          --section=1
          --center="Vita Moonlight Manual"
          --name="VITA-MOONLIGHT"
          --release="vita-moonlight 0.14.5"
          ${CMAKE_CURRENT_SOURCE_DIR}/README.pod
          ${CMAKE_CURRENT_BINARY_DIR}/vita-moonlight.1
  DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/README.pod
)
add_custom_target(docs ALL DEPENDS vita-moonlight.1)

install(
  FILES ${CMAKE_CURRENT_BINARY_DIR}/vita-moonlight.1
  DESTINATION ${CMAKE_INSTALL_MANDIR}/man1
)
